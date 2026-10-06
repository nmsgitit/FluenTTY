using System.IO;
using System.Text;
using FluenTTY.Connections;
using FluenTTY.Files;
using FluenTTY.Models;
using FluenTTY.Remote;
using FluenTTY.Services;
using FluenTTY.Terminal;

// Headless checks for FluenTTY. Needs tests\TestServers running on localhost and FLUENTTY_DATA_DIR
// pointing at a scratch folder (so SettingsStore never touches the real %AppData%).
// Usage: ProtocolTests.exe <test-servers files folder>
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUENTTY_DATA_DIR")))
{
    Console.WriteLine("Set FLUENTTY_DATA_DIR to a scratch folder first.");
    return 2;
}
var filesRoot = args.Length > 0 ? args[0] : "";
int failures = 0, passes = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) passes++; else failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  —  " + detail)}");
}

SettingsStore.Load();
var settings = SettingsStore.Settings;

// ================================================================== emulator
string Screen(TerminalEmulator e) { lock (e.SyncRoot) return string.Join("\n", Enumerable.Range(0, e.Rows).Select(r => e.ScreenLine(r).GetText())); }
string Row(TerminalEmulator e, int r) { lock (e.SyncRoot) return e.ScreenLine(r).GetText(); }
TerminalEmulator Emu(int c = 20, int r = 5) => new(c, r, new TerminalOptions { ScrollbackLines = 100 });
void Feed(TerminalEmulator e, string s) { lock (e.SyncRoot) e.Feed(Encoding.UTF8.GetBytes(s)); }

{
    var e = Emu();
    Feed(e, "hello\r\nworld");
    Check("emu: print and CRLF", Row(e, 0) == "hello" && Row(e, 1) == "world");
    Feed(e, "\x1b[2J\x1b[3;5HX");
    Check("emu: ED 2 + CUP", Row(e, 2) == "    X" && Row(e, 0) == "");
    Feed(e, "\x1b[H" + new string('a', 25));
    Check("emu: autowrap", Row(e, 0) == new string('a', 20) && Row(e, 1) == "aaaaa");
    lock (e.SyncRoot) Check("emu: wrapped flag", e.ScreenLine(0).Wrapped);
    Feed(e, "\x1b[1;31mR\x1b[0m");
    lock (e.SyncRoot) { var c = e.ScreenLine(1).Cells[5]; Check("emu: SGR bold red", c.Fg == 1 && (c.Flags & CellFlags.Bold) != 0, $"fg={c.Fg} flags={c.Flags}"); }
    Feed(e, "\x1b[38;2;10;20;30mT\x1b[48;5;200mU\x1b[38:2::1:2:3mV");
    lock (e.SyncRoot)
    {
        var l = e.ScreenLine(1).Cells;
        Check("emu: truecolor ; form", l[6].Fg == TermColor.Rgb(10, 20, 30));
        Check("emu: 256 bg", l[7].Bg == 200);
        Check("emu: truecolor : form with colourspace", l[8].Fg == TermColor.Rgb(1, 2, 3), $"{l[8].Fg:X}");
    }
}
{
    var e = Emu(10, 5);
    Feed(e, "1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7");
    lock (e.SyncRoot) Check("emu: scrollback grows", e.ScrollbackCount == 2 && Row(e, 0) == "3" && Row(e, 4) == "7", $"sb={e.ScrollbackCount}");
    Feed(e, "\x1b[2;4r\x1b[4;1H\n");
    Check("emu: scroll region keeps rows outside", Row(e, 0) == "3" && Row(e, 4) == "7" && Row(e, 1) == "5", Screen(e));
    Feed(e, "\x1b[r\x1b[?1049h\x1b[HALT");
    lock (e.SyncRoot) Check("emu: alt screen", e.AltScreenActive && Row(e, 0) == "ALT");
    Feed(e, "\x1b[?1049l");
    lock (e.SyncRoot) Check("emu: alt screen restores main", !e.AltScreenActive && Row(e, 0) == "3");
}
{
    var e = Emu(10, 3);
    Feed(e, "日本x");
    lock (e.SyncRoot)
    {
        var cells = e.ScreenLine(0).Cells;
        Check("emu: wide chars take two cells", (cells[0].Flags & CellFlags.WideLead) != 0 && (cells[1].Flags & CellFlags.WideTrail) != 0 && cells[4].Code == 'x');
    }
    Feed(e, "\r\né");
    Check("emu: combining mark attaches", Row(e, 1) == "é");
    Feed(e, "\r\n\x1b(0lqk\x1b(B");
    Check("emu: DEC special graphics", Row(e, 2) == "┌─┐", Row(e, 2));
}
{
    var e = Emu(10, 3);
    var replies = new List<string>();
    e.Reply += b => replies.Add(Encoding.ASCII.GetString(b));
    Feed(e, "\x1b[2;3H\x1b[6n\x1b[c\x1b[>c");
    Check("emu: DSR cursor position", replies.Contains("\x1b[2;3R"), string.Join("|", replies));
    Check("emu: DA replies", replies.Contains("\x1b[?6c") && replies.Contains("\x1b[>0;136;0c"));
    string? title = null;
    e.TitleChanged += t => title = t;
    Feed(e, "\x1b]2;my title\x07\x1b]0;other\x1b\\");
    Check("emu: OSC title (BEL and ST)", title == "other");
    Feed(e, "\x1b[H\x1b[4hZ\x1b[4l");
    Check("emu: insert mode", Row(e, 0).StartsWith("Z"));
    Feed(e, "\x1b[3g\tA");
    Check("emu: tab to end when no stops", Row(e, 0).EndsWith("A"));
}
{
    var e = Emu(10, 4);
    Feed(e, "a\r\nb\r\nc\r\nd\r\ne\r\nf");
    lock (e.SyncRoot) e.Resize(10, 6);
    Check("emu: growing pulls scrollback back", Row(e, 0) == "a" && Row(e, 5) == "f", Screen(e));
    lock (e.SyncRoot) e.Resize(6, 3);
    Check("emu: shrinking pushes to scrollback", Row(e, 2) == "f" && Row(e, 0) == "d", Screen(e));
}

// ================================================================== key encoding
{
    var kb = new KeyboardSettings();
    var st = new KeyEncoder.State();
    Check("keys: arrow normal", KeyEncoder.Encode(System.Windows.Input.Key.Up, System.Windows.Input.ModifierKeys.None, kb, st) == "\x1b[A");
    st.AppCursor = true;
    Check("keys: arrow app mode", KeyEncoder.Encode(System.Windows.Input.Key.Up, System.Windows.Input.ModifierKeys.None, kb, st) == "\x1bOA");
    Check("keys: ctrl+right xterm", KeyEncoder.Encode(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.Control, kb, st) == "\x1b[1;5C");
    Check("keys: F1 xterm216", KeyEncoder.Encode(System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.None, kb, st) == "\x1bOP");
    Check("keys: F5", KeyEncoder.Encode(System.Windows.Input.Key.F5, System.Windows.Input.ModifierKeys.None, kb, st) == "\x1b[15~");
    kb.FunctionKeys = FunctionKeys.Tilde;
    Check("keys: F1 PuTTY tilde", KeyEncoder.Encode(System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.None, kb, st) == "\x1b[11~");
    Check("keys: backspace DEL", KeyEncoder.Encode(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None, kb, st) == "\x7f");
    Check("keys: ctrl+c", KeyEncoder.Encode(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control, kb, st) == "\x03");
    Check("keys: shift+tab", KeyEncoder.Encode(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Shift, kb, st) == "\x1b[Z");
}

// ================================================================== parsing
{
    var p = QuickConnect.Parse("admin@10.1.2.3:2200", ProtocolKind.Ssh)!;
    Check("quick connect user@host:port", p.Username == "admin" && p.Host == "10.1.2.3" && p.Port == 2200);
    var s = QuickConnect.Parse("com7", ProtocolKind.Ssh)!;
    Check("quick connect COM port", s.Protocol == ProtocolKind.Serial && s.Host == "COM7");
    var u = QuickConnect.Parse("sftp://bob@files.example.com:2222", ProtocolKind.Ssh)!;
    Check("quick connect URL", u.Protocol == ProtocolKind.Sftp && u.Username == "bob" && u.Port == 2222);
    var cl = CommandLine.Parse(new[] { "-serial", "COM3", "-sercfg", "115200,8,n,1,N" }, out _);
    Check("command line -serial -sercfg", cl.Count == 1 && cl[0].SerialSpeed == 115200 && cl[0].SerialFlow == SerialFlow.None);
    var cl2 = CommandLine.Parse(new[] { "-ssh", "-P", "2022", "-l", "root", "host.example" }, out _);
    Check("command line -ssh -P -l", cl2.Count == 1 && cl2[0].Port == 2022 && cl2[0].Username == "root" && cl2[0].Host == "host.example");
    var pf = PuttyImport.ParseForward("L8080=intranet:80");
    Check("PuTTY port forward parse", pf != null && pf.SourcePort == 8080 && pf.DestinationHost == "intranet" && pf.DestinationPort == 80);
}

// ================================================================== PuTTY Connection Manager import
{
    var dat = Path.Combine(Path.GetTempPath(), $"pcm-test-{Environment.ProcessId}.dat");
    File.WriteAllText(dat, """
<?xml version="1.0" encoding="utf-8"?>
<configuration version="0.7.1.136" savepassword="True">
  <root type="database" name="IT Department" expanded="True">
    <container type="folder" name="Switches" expanded="True">
      <container type="folder" name="Building A" expanded="True">
        <connection type="PuTTY" name="core-sw1">
          <connection_info><name>core-sw1</name><protocol>SSH</protocol><host>10.0.0.1</host><port>22</port><session>Default Settings</session><commandline>-L 8443:10.0.0.10:443</commandline><description>Core switch</description></connection_info>
          <login><login>netadmin</login><password>secret1</password><prompt /></login>
          <timeout><connectiontimeout>1000</connectiontimeout><logintimeout>750</logintimeout><passwordtimeout>750</passwordtimeout><commandtimeout>750</commandtimeout></timeout>
          <command><command1>terminal length 0</command1><command2>show version</command2><command3 /><command4 /><command5 /></command>
          <options><loginmacro>False</loginmacro><postcommands>True</postcommands><endlinechar>10</endlinechar></options>
        </connection>
      </container>
      <connection type="PuTTY" name="access-sw7">
        <connection_info><name>access-sw7</name><protocol>Telnet</protocol><host>10.0.1.7</host><port>23</port><session>Default Settings</session><commandline /><description /></connection_info>
        <login><login>admin</login><password>pw</password><prompt /></login>
        <command><command1 /><command2 /><command3 /><command4 /><command5 /></command>
        <options><loginmacro>True</loginmacro><postcommands>False</postcommands><endlinechar>10</endlinechar></options>
      </connection>
    </container>
    <connection type="PuTTY" name="weird">
      <connection_info><name>weird</name><protocol>SUPDUP</protocol><host>x</host><port>0</port></connection_info>
    </connection>
  </root>
</configuration>
""");
    var result = PcmImport.Read(dat, includePasswords: true);
    var core = result.Sessions.FirstOrDefault(s => s.Name == "core-sw1");
    var access = result.Sessions.FirstOrDefault(s => s.Name == "access-sw7");
    Check("PCM: two supported connections", result.Sessions.Count == 2, $"{result.Sessions.Count}");
    Check("PCM: nested folders", core?.Folder == "IT Department/Switches/Building A" && access?.Folder == "IT Department/Switches", core?.Folder);
    Check("PCM: post-login commands", core?.InitialCommand == "terminal length 0\nshow version");
    Check("PCM: -L from command line", core?.PortForwards.Count == 1 && core.PortForwards[0].SourcePort == 8443 && core.PortForwards[0].DestinationHost == "10.0.0.10");
    Check("PCM: password protected with DPAPI", SecretStore.IsProtected(core?.Password) && SecretStore.Unprotect(core!.Password) == "secret1");
    Check("PCM: login macro becomes login script", access?.Protocol == ProtocolKind.Telnet && access.LoginScript.Count == 2);
    Check("PCM: unsupported protocol warned", result.Warnings.Any(w => w.Contains("SUPDUP")));
    File.WriteAllBytes(dat, new byte[] { 0x12, 0x34, 0x56, 0x78 });
    try { PcmImport.Read(dat, false); Check("PCM: encrypted file rejected", false); }
    catch (InvalidDataException ex) { Check("PCM: encrypted file rejected", ex.Message.Contains("encrypted")); }
    File.Delete(dat);
}

// ================================================================== settings export/import
{
    settings.Appearance.FontSize = 13;
    SettingsStore.Sessions.Sessions.Add(new SessionProfile { Name = "exported", Host = "h", Password = SecretStore.Protect("pw123") });
    var json = SettingsTransfer.Export(new HashSet<string> { "appearance", "sessions" }, "passphrase1");
    Check("export: password encrypted, not plain", json.Contains("aesgcm:") && !json.Contains("pw123") && !json.Contains("dpapi:"));
    SettingsStore.Sessions.Sessions.Clear();
    settings.Appearance.FontSize = 9;
    var cats = System.Text.Json.Nodes.JsonNode.Parse(json)!["categories"]!.AsObject();
    SettingsTransfer.Import(cats, new HashSet<string> { "appearance", "sessions" }, "passphrase1", true);
    var imported = SettingsStore.Sessions.Sessions.FirstOrDefault(s => s.Name == "exported");
    Check("import: settings category restored", SettingsStore.Settings.Appearance.FontSize == 13, $"{SettingsStore.Settings.Appearance.FontSize}");
    Check("import: password decrypted and re-protected", imported != null && SecretStore.Unprotect(imported.Password) == "pw123");
    SettingsStore.Sessions.Sessions.Clear();
    settings = SettingsStore.Settings;
}

// ================================================================== live protocols
async Task<string> Drain(ITerminalBackend b, TerminalEmulator e, Func<string, bool> until, int ms = 5000)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        var s = Screen(e);
        if (until(s)) return s;
        await Task.Delay(50);
    }
    return Screen(e);
}

async Task TerminalTest(string name, SessionProfile p, Func<ITerminalBackend, TerminalEmulator, Task> body)
{
    var e = new TerminalEmulator(100, 30, TerminalConfig.BuildOptions(settings, p));
    var b = BackendFactory.Create(p, settings);
    b.DataReceived += (buf, n) => { lock (e.SyncRoot) e.Feed(buf.AsSpan(0, n)); };
    e.Reply += d => b.Send(d);
    var ui = new TestUi();
    try
    {
        await Task.Run(() => b.ConnectAsync(ui, 100, 30, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(20));
        await body(b, e);
    }
    catch (Exception ex) { Check($"{name}: connected", false, ex.Message + " | " + string.Join(" / ", ui.Log.TakeLast(4))); }
    finally { b.Dispose(); }
}

await TerminalTest("SSH", new SessionProfile { Protocol = ProtocolKind.Ssh, Host = "127.0.0.1", Port = 2222 }, async (b, e) =>
{
    var s = await Drain(b, e, x => x.Contains("$ "));
    Check("SSH: password auth + shell prompt", s.Contains("test@sandbox"), s);
    Check("SSH: pty size sent", s.Contains("(100x30)"), s);
    Check("SSH: host key stored", SettingsStore.HostKeys.Any(k => k.Port == 2222));
    b.Send(Encoding.UTF8.GetBytes("ls /home\r"));
    s = await Drain(b, e, x => x.Contains("test/"));
    Check("SSH: command output", s.Contains("test/"), s);
    b.Resize(120, 40, 0, 0);
    lock (e.SyncRoot) e.Resize(120, 40);
    b.Send(Encoding.UTF8.GetBytes("size\r"));
    s = await Drain(b, e, x => x.Contains("120x40"));
    Check("SSH: window-change reaches server", s.Contains("120x40"), s);
    string? cwd = null;
    e.WorkingDirectoryChanged += d => cwd = d;
    b.Send(Encoding.UTF8.GetBytes("cd /home/test\r"));
    await Drain(b, e, _ => cwd == "/home/test");
    Check("SSH: OSC 7 working directory", cwd == "/home/test", cwd ?? "null");
    // Screensaver keepalive: an SSH_MSG_IGNORE the shell never sees; the session carries on normally.
    bool sent = b.SendKeepalive();
    b.Send(Encoding.UTF8.GetBytes("echo kept-alive\r"));
    s = await Drain(b, e, x => x.Contains("kept-alive\r\n") || x.Contains("kept-alive\n"));
    Check("SSH: screensaver keepalive sent, session unaffected", sent && s.Contains("kept-alive"), s);
});

// Second SSH connection: host key must now be trusted silently.
{
    var ui = new TestUi();
    var c = SshConnector.Connect(i => new Renci.SshNet.SshClient(i), new SessionProfile { Host = "127.0.0.1", Port = 2222 }, settings, ui, new SshCredentials(), CancellationToken.None);
    Check("SSH: known host key not asked again", !ui.HostKeyAsked);
    c.Dispose();
    var wrong = new TestUi { Password = "wrong" };
    int prompts = 0;
    try
    {
        var creds = new SshCredentials();
        SshConnector.Connect(i => new Renci.SshNet.SshClient(i), new SessionProfile { Host = "127.0.0.1", Port = 2222, Username = "test" }, settings, wrong, creds, CancellationToken.None).Dispose();
        Check("SSH: wrong password rejected", false);
    }
    catch (Exception ex)
    {
        prompts = wrong.Log.Count(l => l.StartsWith("PROMPT"));
        Check("SSH: wrong password re-prompts then fails", prompts >= 2 && ex is Renci.SshNet.Common.SshAuthenticationException, $"{prompts} prompts, {ex.GetType().Name}: {ex.Message}");
    }
}

// SFTP
{
    var ui = new TestUi();
    var fs = new SftpFileSystem(new SessionProfile { Protocol = ProtocolKind.Sftp, Host = "127.0.0.1", Port = 2222, Username = "test" }, settings, new SshCredentials(), ui);
    try
    {
        await fs.ConnectAsync(CancellationToken.None);
        var home = await fs.GetHomeAsync(CancellationToken.None);
        var list = await fs.ListAsync("/home/test", CancellationToken.None);
        Check("SFTP: list directory", list.Any(x => x.Name == "notes.txt"), string.Join(",", list.Select(x => x.Name)));
        var ms = new MemoryStream();
        await fs.DownloadAsync("/home/test/notes.txt", ms, null, CancellationToken.None);
        // Compare with the file on disk: UI tests may have edited it since the server seeded it.
        var expected = filesRoot.Length > 0 ? File.ReadAllText(Path.Combine(filesRoot, "home", "test", "notes.txt")) : null;
        var got = Encoding.UTF8.GetString(ms.ToArray());
        Check("SFTP: download", expected != null ? got == expected : got.StartsWith("Edit me"), got.Length > 60 ? got[..60] : got);
        await fs.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("changed by test\n")), "/home/test/uploaded.txt", null, CancellationToken.None);
        Check("SFTP: upload", filesRoot.Length == 0 || File.ReadAllText(Path.Combine(filesRoot, "home", "test", "uploaded.txt")) == "changed by test\n");
        await fs.RenameAsync("/home/test/uploaded.txt", "/home/test/renamed.txt", CancellationToken.None);
        await fs.CreateDirectoryAsync("/home/test/newdir", CancellationToken.None);
        var after = await fs.ListAsync("/home/test", CancellationToken.None);
        Check("SFTP: rename + mkdir", after.Any(x => x.Name == "renamed.txt") && after.Any(x => x.Name == "newdir" && x.IsDirectory));
        await fs.DeleteAsync(after.First(x => x.Name == "renamed.txt"), CancellationToken.None);
        await fs.DeleteAsync(after.First(x => x.Name == "newdir"), CancellationToken.None);
        var final = await fs.ListAsync("/home/test", CancellationToken.None);
        Check("SFTP: delete", !final.Any(x => x.Name is "renamed.txt" or "newdir"));
    }
    catch (Exception ex) { Check("SFTP: session", false, ex.ToString()); }
    finally { fs.Dispose(); }
}

// Telnet (login script handles the prompts, like PuTTY CM's login macro)
await TerminalTest("Telnet", new SessionProfile { Protocol = ProtocolKind.Telnet, Host = "127.0.0.1", Port = 2323 }, async (b, e) =>
{
    var s = await Drain(b, e, x => x.Contains("login:"));
    Check("Telnet: login prompt", s.Contains("login:"), s);
    b.Send(Encoding.ASCII.GetBytes("test\r"));
    await Drain(b, e, x => x.Contains("Password:"));
    b.Send(Encoding.ASCII.GetBytes("test\r"));
    s = await Drain(b, e, x => x.Contains("$ "));
    Check("Telnet: shell after login", s.Contains("test@sandbox"), s);
    Check("Telnet: NAWS size", s.Contains("(100x30)"), s);
    Check("Telnet: server echo turns local echo off", !b.AutoLocalEcho);
    b.Send(Encoding.ASCII.GetBytes("unicode\r"));
    s = await Drain(b, e, x => x.Contains("│ DEC │"));
    Check("Telnet: DEC graphics through telnet", s.Contains("┌─────┐") && s.Contains("│ DEC │"), s);
    // Screensaver keepalive: IAC NOP is eaten by the telnet layer, nothing reaches the shell's input line.
    bool sent = b.SendKeepalive();
    b.Send(Encoding.ASCII.GetBytes("echo kept-alive\r"));
    s = await Drain(b, e, x => x.Contains("$ echo kept-alive") && x.Split("kept-alive").Length > 2);
    Check("Telnet: screensaver keepalive (NOP) sent, shell input clean", sent && s.Contains("$ echo kept-alive"), s);
});

// Raw
await TerminalTest("Raw", new SessionProfile { Protocol = ProtocolKind.Raw, Host = "127.0.0.1", Port = 2324 }, async (b, e) =>
{
    await Drain(b, e, x => x.Contains("raw echo"));
    b.Send(Encoding.ASCII.GetBytes("abc\r\n"));
    var s = await Drain(b, e, x => x.Contains("echo: cba"));
    Check("Raw: round trip", s.Contains("echo: cba"), s);
    Check("Raw: auto local echo on", b.AutoLocalEcho);
    Check("Raw: no screensaver keepalive (it would reach the remote program)", !b.SendKeepalive());
});

// FTP
{
    var ui = new TestUi();
    var fs = new FtpFileSystem(new SessionProfile { Protocol = ProtocolKind.Ftp, Host = "127.0.0.1", Port = 2121, Username = "test", FtpEncryption = FtpEncryption.None }, settings, ui);
    try
    {
        await fs.ConnectAsync(CancellationToken.None);
        var list = await fs.ListAsync("/home/test", CancellationToken.None);
        Check("FTP: list", list.Any(x => x.Name == "notes.txt"), string.Join(",", list.Select(x => x.Name)));
        var ms = new MemoryStream();
        await fs.DownloadAsync("/home/test/config.json", ms, null, CancellationToken.None);
        Check("FTP: download", Encoding.UTF8.GetString(ms.ToArray()).Contains("switch-01"));
        await fs.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("ftp upload")), "/home/test/ftp.txt", null, CancellationToken.None);
        Check("FTP: upload", filesRoot.Length == 0 || File.ReadAllText(Path.Combine(filesRoot, "home", "test", "ftp.txt")) == "ftp upload");
        var up = (await fs.ListAsync("/home/test", CancellationToken.None)).First(x => x.Name == "ftp.txt");
        await fs.DeleteAsync(up, CancellationToken.None);
        Check("FTP: delete", !(await fs.ListAsync("/home/test", CancellationToken.None)).Any(x => x.Name == "ftp.txt"));
    }
    catch (Exception ex) { Check("FTP: session", false, ex.ToString()); }
    finally { fs.Dispose(); }
}

// VNC
foreach (var (label, enc, depth) in new[] { ("auto/ZRLE 24-bit", VncEncoding.Auto, VncColorDepth.TrueColor), ("Raw 24-bit", VncEncoding.Raw, VncColorDepth.TrueColor), ("ZRLE 16-bit", VncEncoding.Zrle, VncColorDepth.HighColor) })
{
    var vnc = new VncClient();
    var v = new VncSettings { Encoding = enc, ColorDepth = depth };
    int updates = 0;
    vnc.FrameUpdated += (_, _, _, _) => Interlocked.Increment(ref updates);
    try
    {
        await vnc.ConnectAsync("127.0.0.1", 5959, settings, false, () => "test", v, false, _ => { }, CancellationToken.None);
        for (int i = 0; i < 60 && updates == 0; i++) await Task.Delay(50);
        int px;
        lock (vnc.FrameLock) px = vnc.Frame[60 * vnc.Width + 60];
        bool colourOk = depth == VncColorDepth.TrueColor ? px == 0xE81123 : Math.Abs(((px >> 16) & 255) - 0xE8) < 12 && Math.Abs((px & 255) - 0x23) < 12;
        Check($"VNC {label}: framebuffer decoded", updates > 0 && vnc.Width == 800 && colourOk, $"updates={updates} px={px:X6}");
        vnc.SendPointer(1, 400, 500);
        vnc.SendPointer(0, 400, 500);
        await Task.Delay(300);
        lock (vnc.FrameLock) px = vnc.Frame[500 * vnc.Width + 400];
        Check($"VNC {label}: pointer event round trip", (px & 0xF0F0F0) == 0xF0F0F0, $"{px:X6}");
    }
    catch (Exception ex) { Check($"VNC {label}", false, ex.Message); }
    finally { vnc.Dispose(); }
}
{
    var vnc = new VncClient();
    try
    {
        await vnc.ConnectAsync("127.0.0.1", 5959, settings, false, () => "nope", new VncSettings(), false, _ => { }, CancellationToken.None);
        Check("VNC: wrong password rejected", false);
    }
    catch (UnauthorizedAccessException) { Check("VNC: wrong password rejected", true); }
    catch (Exception ex) { Check("VNC: wrong password rejected", false, ex.Message); }
    finally { vnc.Dispose(); }
}

// ================================================================== security review regressions
// Versioning, change log and updater (no network: GitHub's JSON is canned)
{
    var current = Updater.Normalize(Version.Parse(AppInfo.Version));
    var entry = Changelog.All.FirstOrDefault(e => e.Version == current);
    Check("version: CHANGELOG.md has a section for this version", entry != null && entry.Items.Count > 0, $"{AppInfo.Version}: {string.Join(", ", Changelog.All.Select(e => e.Version))}");
    Check("version: this version is the newest in the change log", Changelog.All.Count > 0 && Changelog.All[0].Version == current);
    var between = Changelog.Between(new Version(1, 0, 0), current);
    Check("changelog: entries since an older version, newest first", between.Count >= 1 && between[0].Version == current && between.All(e => e.Version > new Version(1, 0, 0)));

    const string repo = "someone/FluenTTY";
    var json = """
    {
      "tag_name": "v9.8.7", "draft": false, "prerelease": false,
      "html_url": "https://github.com/someone/FluenTTY/releases/tag/v9.8.7",
      "body": "- One\n- Two",
      "assets": [
        { "name": "FluenTTY-Setup-9.8.7.exe", "size": 10, "digest": "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
          "browser_download_url": "https://github.com/someone/FluenTTY/releases/download/v9.8.7/FluenTTY-Setup-9.8.7.exe" },
        { "name": "FluenTTY-Portable-9.8.7.exe", "size": 20,
          "browser_download_url": "https://evil.example/FluenTTY-Portable-9.8.7.exe" },
        { "name": "SHA256SUMS.txt", "size": 1,
          "browser_download_url": "https://github.com/someone/FluenTTY/releases/download/v9.8.7/SHA256SUMS.txt" }
      ]
    }
    """;
    var rel = Updater.ParseRelease(json, repo);
    Check("updater: reads version, notes and setup digest", rel != null && rel.Number == "9.8.7" && rel.Setup?.Sha256 == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789" && rel.Notes.Contains("Two"));
    Check("SEC U1 updater ignores downloads from outside the repository", rel != null && rel.Portable == null && rel.Checksums != null);
    Check("updater: drafts and pre-releases are never offered",
        Updater.ParseRelease(json.Replace("\"prerelease\": false", "\"prerelease\": true"), repo) == null &&
        Updater.ParseRelease(json.Replace("\"draft\": false", "\"draft\": true"), repo) == null);
    Check("updater: release of another repository has no usable downloads", Updater.ParseRelease(json, "other/FluenTTY")?.Assets.Count == 0);
    var sums = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  FluenTTY-Setup-9.8.7.exe\nzz  FluenTTY-Portable-9.8.7.exe\n";
    Check("updater: SHA256SUMS.txt lookup (well-formed lines only)",
        Updater.FindChecksum(sums, "FluenTTY-Setup-9.8.7.exe") == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" &&
        Updater.FindChecksum(sums, "FluenTTY-Portable-9.8.7.exe") == null);

    // Backups before an update copy every data file and leave the originals alone.
    SettingsStore.SaveSettingsNow();
    var before = File.ReadAllText(SettingsStore.SettingsPath);
    var backup = Updater.BackupUserData("test");
    Check("updater: user data backed up before updating", File.Exists(Path.Combine(backup, "settings.json")) && File.ReadAllText(Path.Combine(backup, "settings.json")) == before && File.ReadAllText(SettingsStore.SettingsPath) == before);
    Directory.Delete(backup, true);
}

await SecurityTests.Run(Check, filesRoot);

Console.WriteLine($"\n{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

sealed class TestUi : IConnectionUi
{
    public List<string> Log = new();
    public string? Password = "test";
    public bool HostKeyAsked;
    public string? Prompt(string prompt, bool echo) { Log.Add("PROMPT " + prompt); return prompt.Contains("login", StringComparison.OrdinalIgnoreCase) || prompt.Contains("User", StringComparison.OrdinalIgnoreCase) ? "test" : Password; }
    public bool? ConfirmHostKey(HostKeyInfo info) { HostKeyAsked = true; Log.Add($"HOSTKEY {info.Algorithm} {info.FingerprintSha256}"); return true; }
    public bool Confirm(string t, string m, string y, string n) => true;
    public void Status(string m) => Log.Add("STATUS " + m);
    public void Display(string t) => Log.Add("DISPLAY " + t);
}

