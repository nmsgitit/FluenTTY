using System.IO;
using System.Text.Json.Nodes;
using FluenTTY.Connections;
using FluenTTY.Files;
using FluenTTY.Models;
using FluenTTY.Remote;
using FluenTTY.Services;

/// <summary>
/// Regression tests for the security review (High/Medium findings). Each test drives the real code
/// against a hostile input: the TestServers' /evil SFTP tree and VNC port 5960, a key-type swap,
/// a plaintext-only FTP server, and poisoned settings / PuTTY CM files.
/// </summary>
static class SecurityTests
{
    public static async Task Run(Action<string, bool, string> check, string filesRoot)
    {
        try { await RunAll(check, filesRoot); }
        catch (Exception ex) { check("SEC suite", false, ex.ToString()); }
    }

    private static async Task RunAll(Action<string, bool, string> check, string filesRoot)
    {
        void Check(string name, bool ok, string detail = "") => check("SEC " + name, ok, detail);
        var settings = SettingsStore.Settings;

        // ------------------------------------------------------------ H1: remote names never escape the download folder
        {
            Check("H1 name: backslash traversal reduced to a file name", LocalPaths.SafeFileName(@"..\..\x.txt") == "x.txt", LocalPaths.SafeFileName(@"..\..\x.txt"));
            Check("H1 name: rooted path reduced to a file name", LocalPaths.SafeFileName(@"C:\Users\v\Startup\run.bat") == "run.bat");
            Check("H1 name: '..' and empty become '_'", LocalPaths.SafeFileName("..") == "_" && LocalPaths.SafeFileName("") == "_");
            Check("H1 name: alternate data stream colon removed", !LocalPaths.SafeFileName("a.txt:evil.exe").Contains(':'));
            Check("H1 name: device names defused", LocalPaths.SafeFileName("CON.txt") == "_CON.txt" && LocalPaths.SafeFileName("nul") == "_nul");
            Check("H1 name: bidi override removed", LocalPaths.SafeFileName("invoice\u202Etxt.bat") == "invoicetxt.bat", LocalPaths.SafeFileName("invoice\u202Etxt.bat"));
            var baseDir = Path.Combine(Path.GetTempPath(), "fluentty-h1-" + Environment.ProcessId);
            Check("H1 ChildPath stays inside", LocalPaths.IsInside(LocalPaths.ChildPath(baseDir, @"..\..\..\Windows\evil.dll"), baseDir));

            // Real download of the hostile /evil tree from the test SFTP server.
            var dataDir = Environment.GetEnvironmentVariable("FLUENTTY_DATA_DIR")!;
            var area = Path.Combine(dataDir, "h1");
            var target = Path.Combine(area, "target");
            if (Directory.Exists(area)) Directory.Delete(area, true);
            Directory.CreateDirectory(target);
            var rooted = filesRoot.Length > 0 ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filesRoot))!, "rooted-escape.txt") : null;
            if (rooted != null && File.Exists(rooted)) File.Delete(rooted);

            var fs = new SftpFileSystem(new SessionProfile { Protocol = ProtocolKind.Sftp, Host = "127.0.0.1", Port = 2222, Username = "test" }, settings, new SshCredentials(), new SecUi());
            try
            {
                await fs.ConnectAsync(CancellationToken.None);
                var evil = (await fs.ListAsync("/", CancellationToken.None)).First(e => e.Name == "evil");
                var skipped = new List<string>();
                int files = 0;
                await LocalPaths.DownloadTree(fs, evil, target, async (e, local) =>
                {
                    files++;
                    await using var o = File.Create(local);
                    await fs.DownloadAsync(e.FullPath, o, null, CancellationToken.None);
                }, skipped, CancellationToken.None);
                var written = Directory.GetFiles(area, "*", SearchOption.AllDirectories);
                Check("H1 hostile tree: every file inside the chosen folder", written.Length > 0 && written.All(f => LocalPaths.IsInside(f, target)),
                    string.Join(" | ", written.Where(f => !LocalPaths.IsInside(f, target))));
                Check("H1 hostile tree: rooted name did not write outside", rooted == null || !File.Exists(rooted));
                Check("H1 hostile tree: content really downloaded", written.Any(f => File.ReadAllText(f).StartsWith("HOSTILE")));
                Check("H1 hostile tree: endless folder nesting stopped", skipped.Any(s => s.Contains("nested more than")) && files < 200, $"files={files} skipped={string.Join(" | ", skipped.Take(3))}");
                Check("H1 hostile tree: no bidi characters in saved names", written.All(f => !Path.GetFileName(f).Any(LocalPaths.IsBidiOrInvisible)));
            }
            catch (Exception ex) { Check("H1 hostile tree download", false, ex.ToString()); }
            finally { fs.Dispose(); }
            try { Directory.Delete(area, true); } catch { }
        }

        // ------------------------------------------------------------ M1: runnable files are recognised and marked
        {
            Check("M1 runnable extensions recognised", new[] { "a.exe", "b.BAT", "c.lnk", "d.hta", "e.msi", "f.ps1", "g.url", "invoicetxt.bat", "h.iso" }.All(LocalPaths.IsRunnable));
            Check("M1 documents not treated as runnable", !new[] { "notes.txt", "config.json", "script.py", "readme" }.Any(LocalPaths.IsRunnable));
            var f = Path.Combine(Path.GetTempPath(), $"fluentty-motw-{Environment.ProcessId}.txt");
            File.WriteAllText(f, "x");
            LocalPaths.MarkFromInternet(f, "sftp://host/x.txt");
            string zone = "";
            try { zone = File.ReadAllText(f + ":Zone.Identifier"); } catch (Exception ex) { zone = ex.Message; }
            Check("M1 Mark-of-the-Web written", zone.Contains("ZoneId=3") && zone.Contains("sftp://host/x.txt"), zone);
            LocalPaths.MarkFromInternet(f, "sftp://host/a\r\nZoneId=0‮b.txt");
            try { zone = File.ReadAllText(f + ":Zone.Identifier"); } catch (Exception ex) { zone = ex.Message; }
            Check("M1 Mark-of-the-Web can't be rewritten by a file name", zone.Split('\n').Count(l => l.StartsWith("ZoneId")) == 1 && zone.Contains("ZoneId=3"), zone);
            File.Delete(f);
        }

        // ------------------------------------------------------------ M5: single-instance pipe only for this user
        if (!new Func<bool>(() => { using var other = new SingleInstance(); return other.IsFirstInstance; })())
        {
            // Someone's real FluenTTY owns the pipe: probing would open tabs in their window. Don't touch it.
            Check("M5 pipe (skipped: close every FluenTTY window and run again)", false);
        }
        else
        {
            using var inst = new SingleInstance();
            string[]? got = null;
            inst.ArgsReceived += a => got = a;
            inst.StartListening();
            await Task.Delay(300);
            try
            {
                using (var probe = new System.IO.Pipes.NamedPipeClientStream(".", SingleInstance.PipeName, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.CurrentUserOnly))
                {
                    probe.Connect(2000);
                    var acl = System.IO.Pipes.PipesAclExtensions.GetAccessControl(probe);
                    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                    var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.AccessRule>().ToList();
                    Check("M5 pipe access list names only this user", rules.Count > 0 && rules.All(r => r.IdentityReference.Equals(me)),
                        string.Join(", ", rules.Select(r => r.IdentityReference.Value)));
                }
                await Task.Delay(300);
                got = null; // the probe connection above arrives as an empty argument list
                Check("M5 pipe name includes the session", SingleInstance.PipeName.EndsWith("." + System.Diagnostics.Process.GetCurrentProcess().SessionId));
                bool sent = SingleInstance.SendToExisting(new[] { "-ssh", "example" });
                for (int i = 0; i < 40 && got == null; i++) await Task.Delay(50);
                Check("M5 command lines still reach the running instance", sent && got?.SequenceEqual(new[] { "-ssh", "example" }) == true);
            }
            catch (Exception ex) { Check("M5 pipe", false, ex.ToString()); }
        }

        // ------------------------------------------------------------ L5 / badge
        {
            var attr = typeof(SettingsStore).Assembly.GetCustomAttributes(typeof(System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute), false)
                .Cast<System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute>().FirstOrDefault();
            Check("L5 P/Invoke DLLs load from System32 only", attr?.Paths == System.Runtime.InteropServices.DllImportSearchPath.System32, attr?.Paths.ToString() ?? "no attribute");
            Check("Badge: cleartext protocols flagged", new[] { ProtocolKind.Telnet, ProtocolKind.Rlogin, ProtocolKind.Rsh, ProtocolKind.Raw, ProtocolKind.Vnc, ProtocolKind.Xdmcp }.All(FluenTTY.Views.SessionTab.IsCleartextProtocol)
                && !new[] { ProtocolKind.Ssh, ProtocolKind.Sftp, ProtocolKind.Rdp, ProtocolKind.Serial }.Any(FluenTTY.Views.SessionTab.IsCleartextProtocol));
        }

        // ------------------------------------------------------------ M6: XDMCP host can't carry X server options
        Check("M6 XDMCP host validation", XdmcpTab.IsSafeHost("10.0.0.1") && XdmcpTab.IsSafeHost("unix-host.example.com") && XdmcpTab.IsSafeHost("[fe80::1%3]")
            && !XdmcpTab.IsSafeHost("10.0.0.1 -ac") && !XdmcpTab.IsSafeHost("-ac") && !XdmcpTab.IsSafeHost("host\" -logfile x"));

        // ------------------------------------------------------------ M2: host key of a different type
        {
            SettingsStore.HostKeys.RemoveAll(k => k.Port == 2222);
            SettingsStore.HostKeys.Add(new KnownHostKey { Host = "127.0.0.1", Port = 2222, Algorithm = "ssh-fake-test", FingerprintSha256 = "SHA256:previously-known", KeyBase64 = "AAAA" });
            var ui = new SecUi();
            try
            {
                SshConnector.Connect(i => new Renci.SshNet.SshClient(i), new SessionProfile { Host = "127.0.0.1", Port = 2222 }, settings, ui, new SshCredentials(), CancellationToken.None).Dispose();
            }
            catch (Exception ex) { ui.Log.Add(ex.Message); }
            Check("M2 key-type swap is reported as a changed key", ui.HostKey?.Mismatch == true,
                ui.HostKey == null ? "no host key prompt" : $"Mismatch={ui.HostKey.Mismatch}");
            SettingsStore.HostKeys.RemoveAll(k => k.Algorithm == "ssh-fake-test");
        }

        // ------------------------------------------------------------ M4: FTP must not silently fall back to plaintext
        foreach (bool allow in new[] { false, true })
        {
            var ui = new SecUi { ConfirmAnswer = allow };
            var fs = new FtpFileSystem(new SessionProfile { Protocol = ProtocolKind.Ftp, Host = "127.0.0.1", Port = 2121, Username = "test", Password = SecretStore.Protect("test") }, settings, ui);
            bool connected = false;
            try { await fs.ConnectAsync(CancellationToken.None); connected = fs.IsConnected; }
            catch (Exception ex) { ui.Log.Add(ex.GetType().Name + ": " + ex.Message); }
            finally { fs.Dispose(); }
            bool asked = ui.Confirms.Any(c => c.Contains("encrypt", StringComparison.OrdinalIgnoreCase));
            if (!allow) Check("M4 plaintext FTP refused when the user declines", asked && !connected, $"asked={asked} connected={connected} | {string.Join(" / ", ui.Log.TakeLast(2))}");
            else Check("M4 plaintext FTP only after the user agrees", asked && connected, $"asked={asked} connected={connected} | {string.Join(" / ", ui.Log.TakeLast(2))}");
        }

        // ------------------------------------------------------------ M6: poisoned settings file
        {
            var original = Json.DeepClone(SettingsStore.Settings);
            // Start clean: an earlier run may have saved the test key to known_hosts.json.
            SettingsStore.HostKeys.RemoveAll(k => k.FingerprintSha256 == "SHA256:attacker");
            SettingsStore.SaveHostKeys();
            var cats = JsonNode.Parse("""
            {
              "appearance": { "appearance": { "fontSize": 15 } },
              "interface": { "interface": { "checkForUpdates": false } },
              "files": { "files": { "certificates": "AcceptAll", "ftpEncryption": "None" }, "editor": { "externalEditor": "C:\\evil\\payload.exe" } },
              "remote": { "xdmcp": { "xServerPath": "C:\\evil\\x.exe", "extraArguments": "-ac" }, "rdp": { "serverAuthentication": "ConnectAnyway" } },
              "logging": { "logging": { "mode": "AllOutput", "fileName": "%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Startup\\&H.cmd" } },
              "ssh": { "ssh": { "manualHostKeys": [ "SHA256:attacker" ], "localPortsAcceptAll": true, "privateKeyFile": "\\\\attacker\\share\\id_rsa" } },
              "connection": { "proxy": { "type": "Socks5", "host": "evil-proxy.example", "port": 1080 } },
              "hostkeys": [ { "host": "10.9.9.9", "port": 22, "algorithm": "ssh-ed25519", "fingerprintSha256": "SHA256:attacker", "keyBase64": "AAAA" } ],
              "sessions": { "sessions": [ { "name": "poisoned", "host": "10.9.9.9", "privateKeyFile": "\\\\attacker\\share\\key.ppk" } ], "folders": [] }
            }
            """)!.AsObject();
            var all = cats.Select(kv => kv.Key).ToHashSet();
            SettingsTransfer.Import(cats, all, null, true);
            var s = SettingsStore.Settings;
            var poisoned = SettingsStore.Sessions.Sessions.FirstOrDefault(x => x.Name == "poisoned");
            Check("M6 harmless settings still import", s.Appearance.FontSize == 15, $"{s.Appearance.FontSize}");
            Check("M6 external editor not taken from an import", s.Editor.ExternalEditor == original.Editor.ExternalEditor, s.Editor.ExternalEditor);
            Check("M6 X server path/arguments not taken from an import", s.Xdmcp.XServerPath == original.Xdmcp.XServerPath && s.Xdmcp.ExtraArguments == original.Xdmcp.ExtraArguments, $"{s.Xdmcp.XServerPath} {s.Xdmcp.ExtraArguments}");
            Check("M6 log file location not taken from an import", s.Logging.FileName == original.Logging.FileName, s.Logging.FileName);
            Check("M6 manual host keys not taken from an import", s.Ssh.ManualHostKeys.Count == 0, string.Join(",", s.Ssh.ManualHostKeys));
            Check("M6 weaker certificate/FTP/RDP policies not taken from an import",
                s.Files.Certificates != CertificatePolicy.AcceptAll && s.Files.FtpEncryption != FtpEncryption.None && s.Rdp.ServerAuthentication != RdpServerAuth.ConnectAnyway,
                $"{s.Files.Certificates} {s.Files.FtpEncryption} {s.Rdp.ServerAuthentication}");
            Check("M6 port-forward exposure not taken from an import", !s.Ssh.LocalPortsAcceptAll);
            Check("M6 proxy not taken from an import", s.Proxy.Host == original.Proxy.Host && s.Proxy.Type == original.Proxy.Type, $"{s.Proxy.Type} {s.Proxy.Host}");
            Check("M6 network key paths not taken from an import", !(s.Ssh.PrivateKeyFile ?? "").StartsWith(@"\\") && !(poisoned?.PrivateKeyFile ?? "").StartsWith(@"\\"),
                $"{s.Ssh.PrivateKeyFile} / {poisoned?.PrivateKeyFile}");
            Check("M6 host keys not trusted from an import without review", !SettingsStore.HostKeys.Any(k => k.FingerprintSha256 == "SHA256:attacker"));
            Check("M6 update checks not turned off by an import without review", s.Interface.CheckForUpdates == original.Interface.CheckForUpdates);
            SettingsStore.HostKeys.RemoveAll(k => k.FingerprintSha256 == "SHA256:attacker");
            SettingsStore.Sessions.Sessions.RemoveAll(x => x.Name == "poisoned");
            SettingsStore.ReplaceSettings(Json.DeepClone(original));

            // With every risk approved, the same file applies in full (the review is a gate, not a filter).
            var risks = SettingsTransfer.FindRisks(cats);
            Check("M6 review lists every risky change", risks.Count >= 13 && risks.Any(r => r.Id == "interface.checkForUpdates"), string.Join(", ", risks.Select(r => r.Id)));
            SettingsTransfer.Import(cats, all, null, true, risks.Select(r => r.Id).ToHashSet());
            s = SettingsStore.Settings;
            Check("M6 approved changes are applied", s.Editor.ExternalEditor == @"C:\evil\payload.exe" && s.Proxy.Host == "evil-proxy.example" &&
                  SettingsStore.HostKeys.Any(k => k.FingerprintSha256 == "SHA256:attacker") && !s.Interface.CheckForUpdates);
            SettingsStore.HostKeys.RemoveAll(k => k.FingerprintSha256 == "SHA256:attacker");
            SettingsStore.SaveHostKeys();
            SettingsStore.Sessions.Sessions.RemoveAll(x => x.Name == "poisoned");
            SettingsStore.ReplaceSettings(original);
        }

        // ------------------------------------------------------------ M6: PuTTY CM database with network key paths
        {
            var dat = Path.Combine(Path.GetTempPath(), $"pcm-sec-{Environment.ProcessId}.dat");
            File.WriteAllText(dat, """
            <configuration version="0.7.1.136" savepassword="True"><root type="database" name="DB">
              <connection type="PuTTY" name="unc-key">
                <connection_info><name>unc-key</name><protocol>SSH</protocol><host>10.0.0.5</host><port>22</port><session>Default Settings</session><commandline>-i \\attacker\share\key.ppk</commandline></connection_info>
                <login><login>admin</login><password /></login>
                <options><loginmacro>False</loginmacro><ssh_public_key>\\attacker\share\other.ppk</ssh_public_key></options>
              </connection>
            </root></configuration>
            """);
            try
            {
                var r = PcmImport.Read(dat, includePasswords: false);
                var p = r.Sessions.FirstOrDefault();
                Check("M6 PuTTY CM network key path dropped with a warning", p != null && !(p.PrivateKeyFile ?? "").StartsWith(@"\\") && r.Warnings.Any(w => w.Contains(@"\\attacker")),
                    $"key={p?.PrivateKeyFile} warnings={string.Join(" | ", r.Warnings)}");
            }
            finally { File.Delete(dat); }
        }

        // ------------------------------------------------------------ M7: VNC must prefer authentication over "None"
        {
            var vnc = new VncClient();
            bool asked = false;
            try
            {
                await vnc.ConnectAsync("127.0.0.1", 5960, settings, false, () => { asked = true; return "test"; }, new VncSettings(), false, _ => { }, CancellationToken.None);
            }
            catch (Exception ex) { Check("M7 VNC connect", false, ex.Message); }
            finally { vnc.Dispose(); }
            Check("M7 VNC picks password authentication when the server also offers None", asked);
        }
    }
}

sealed class SecUi : IConnectionUi
{
    public List<string> Log = new();
    public List<string> Confirms = new();
    public bool ConfirmAnswer = true;
    public HostKeyInfo? HostKey;
    public string? Prompt(string prompt, bool echo) => prompt.Contains("login", StringComparison.OrdinalIgnoreCase) ? "test" : "test";
    public bool? ConfirmHostKey(HostKeyInfo info) { HostKey = info; return true; }
    public bool Confirm(string t, string m, string y, string n) { Confirms.Add(t + ": " + m); return ConfirmAnswer; }
    public void Status(string m) => Log.Add("STATUS " + m);
    public void Display(string t) => Log.Add("DISPLAY " + t);
}
