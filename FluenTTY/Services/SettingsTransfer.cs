using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using FluenTTY.Models;
using FluenTTY.Views;
using Microsoft.Win32;

namespace FluenTTY.Services;

/// <summary>
/// Export / import of settings by category for moving between PCs (Settings › Backup &amp; import).
/// The file is JSON: { format, version, exported, categories: { key: data } }. Saved passwords are
/// DPAPI-bound to this Windows account, so they're either left out or re-encrypted with a passphrase
/// the user picks (AES-256-GCM) and decrypted on import.
/// </summary>
public static class SettingsTransfer
{
    public const string Format = "FluenTTY settings";
    public const string FileFilter = "FluenTTY settings (*.fluentty.json)|*.fluentty.json|JSON files (*.json)|*.json|All files|*.*";

    public sealed record Category(string Key, string Title, string Description, string[] Sections);

    /// <summary>Categories; "sections" are AppSettings property names (camelCase in JSON).</summary>
    public static readonly Category[] Categories =
    {
        new("interface", "Interface", "Theme, backdrop, animations, tabs and window behaviour", new[] { "interface" }),
        new("appearance", "Appearance and colours", "Font, cursor and colour schemes (including your custom schemes)", new[] { "appearance" }),
        new("terminal", "Terminal", "Emulation, features, keyboard, bell, selection and character set", new[] { "terminal", "keyboard", "bell", "selection", "translation" }),
        new("logging", "Logging", "Session log options", new[] { "logging" }),
        new("connection", "Connection and proxy", "Timeouts, keepalives, terminal type, environment, proxy", new[] { "connection", "proxy" }),
        new("ssh", "SSH", "Algorithms, authentication, tunnels and SFTP browser options", new[] { "ssh" }),
        new("telnet", "Telnet, Rlogin and Rsh", "Negotiation and user name options", new[] { "telnet", "rlogin" }),
        new("serial", "Serial", "Default line, speed and framing", new[] { "serial" }),
        new("remote", "Remote desktop", "RDP, VNC and XDMCP options", new[] { "rdp", "vnc", "xdmcp" }),
        new("files", "File transfer and editor", "FTP/SFTP behaviour and the text editor", new[] { "files", "editor" }),
        new("sessions", "Saved sessions", "All saved sessions and folders", Array.Empty<string>()),
        new("hostkeys", "Known host keys", "SSH host keys you've accepted", Array.Empty<string>()),
    };

    // ================================================================== export

    public static void ExportInteractive(Window? owner)
    {
        var panel = new StackPanel();
        var intro = new TextBlock { Text = "Choose what to include. The file can be imported into FluenTTY on another PC.", TextWrapping = TextWrapping.Wrap };
        intro.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        panel.Children.Add(intro);
        var boxes = new Dictionary<string, CheckBox>();
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in Categories)
        {
            var cb = new CheckBox { Content = c.Title, IsChecked = true, ToolTip = c.Description, Margin = new Thickness(0, 4, 8, 4) };
            boxes[c.Key] = cb;
            grid.Children.Add(cb);
        }
        panel.Children.Add(grid);
        var secrets = new CheckBox { Content = "Include saved passwords, protected with a passphrase", Margin = new Thickness(0, 12, 0, 0) };
        var pass1 = new PasswordBox { Margin = new Thickness(0, 6, 0, 0), IsEnabled = false };
        var pass2 = new PasswordBox { Margin = new Thickness(0, 6, 0, 0), IsEnabled = false };
        System.Windows.Automation.AutomationProperties.SetName(pass1, "Passphrase");
        System.Windows.Automation.AutomationProperties.SetName(pass2, "Confirm passphrase");
        secrets.Checked += (_, _) => { pass1.IsEnabled = pass2.IsEnabled = true; pass1.Focus(); };
        secrets.Unchecked += (_, _) => pass1.IsEnabled = pass2.IsEnabled = false;
        var note = new TextBlock { Text = "Without this, passwords are left out: they're encrypted for your Windows account and wouldn't work elsewhere.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        panel.Children.Add(secrets);
        panel.Children.Add(note);
        panel.Children.Add(pass1);
        panel.Children.Add(pass2);

        var dlg = new FluentDialog(owner, "Export settings", panel, "Export…") { Width = 560 };
        dlg.Validate = () =>
        {
            if (secrets.IsChecked == true && (pass1.Password.Length < 6 || pass1.Password != pass2.Password))
            {
                FluentDialog.Info(dlg, "Passphrase", pass1.Password.Length < 6 ? "Use a passphrase of at least 6 characters." : "The passphrases don't match.");
                return false;
            }
            return true;
        };
        if (dlg.ShowDialog() != DialogResultKind.Primary) return;

        var save = new SaveFileDialog { Filter = FileFilter, FileName = $"FluenTTY settings {DateTime.Now:yyyy-MM-dd}.fluentty.json", Title = "Export settings" };
        if (save.ShowDialog(owner) != true) return;
        var chosen = boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        var json = Export(chosen, secrets.IsChecked == true ? pass1.Password : null);
        File.WriteAllText(save.FileName, json);
        FluentDialog.Info(owner, "Export finished", $"Saved {chosen.Count} categor{(chosen.Count == 1 ? "y" : "ies")} to\n{save.FileName}");
    }

    public static string Export(ISet<string> categories, string? passphrase)
    {
        var settingsNode = JsonSerializer.SerializeToNode(SettingsStore.Settings, Json.Options)!.AsObject();
        var cats = new JsonObject();
        foreach (var c in Categories.Where(c => categories.Contains(c.Key)))
        {
            switch (c.Key)
            {
                case "sessions":
                {
                    var data = Json.DeepClone(SettingsStore.Sessions);
                    data.LastOpen.Clear();
                    data.Recent.Clear();
                    foreach (var s in data.Sessions) s.Password = ReprotectForExport(s.Password, passphrase);
                    cats[c.Key] = JsonSerializer.SerializeToNode(new { data.Sessions, data.Folders }, Json.Options);
                    break;
                }
                case "hostkeys":
                    cats[c.Key] = JsonSerializer.SerializeToNode(SettingsStore.HostKeys, Json.Options);
                    break;
                default:
                {
                    var obj = new JsonObject();
                    foreach (var section in c.Sections)
                        obj[section] = settingsNode[section]?.DeepClone();
                    if (c.Key == "connection" && obj["proxy"] is JsonObject proxy)
                        proxy["password"] = ReprotectForExport(proxy["password"]?.GetValue<string>(), passphrase) ?? "";
                    cats[c.Key] = obj;
                    break;
                }
            }
        }
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["app"] = AppInfo.Version,
            ["exported"] = DateTime.Now.ToString("s"),
            ["passwordsIncluded"] = passphrase != null,
            ["categories"] = cats,
        };
        return root.ToJsonString(Json.Options);
    }

    private static string? ReprotectForExport(string? stored, string? passphrase)
    {
        if (string.IsNullOrEmpty(stored) || passphrase == null) return null;
        var plain = SecretStore.Unprotect(stored);
        return plain == null ? null : SecretStore.EncryptForExport(plain, passphrase);
    }

    // ================================================================== import

    // ================================================================== import review
    //
    // A settings file is a list of instructions from whoever wrote it. Values that run programs, write
    // files, trust keys or weaken encryption are only applied when the user ticks them one by one;
    // otherwise the current value is kept. Security review M6 — see CLAUDE.md "Security invariants".

    /// <summary>One security-relevant change an import would make.</summary>
    public sealed record ImportRisk(string Id, string Title, string Detail);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static bool Is(JsonNode? n, string value) => string.Equals(Str(n), value, StringComparison.OrdinalIgnoreCase);
    public static bool IsNetworkPath(string? path) => !string.IsNullOrWhiteSpace(path) && (path.Trim('"', ' ').StartsWith(@"\\") || path.Trim('"', ' ').StartsWith("//"));

    private static JsonObject CurrentSettingsNode() => JsonSerializer.SerializeToNode(SettingsStore.Settings, Json.Options)!.AsObject();

    /// <summary>The risky changes in an export file's categories (compared with the current settings).</summary>
    public static List<ImportRisk> FindRisks(JsonObject cats)
    {
        var cur = CurrentSettingsNode();
        var risks = new List<ImportRisk>();
        JsonNode? In(string cat, string section, string prop) => cats[cat]?[section]?[prop];
        bool Changed(string cat, string section, string prop) =>
            In(cat, section, prop) is JsonNode n && !JsonNode.DeepEquals(n, cur[section]?[prop]);

        if (Changed("files", "editor", "externalEditor") && !string.IsNullOrWhiteSpace(Str(In("files", "editor", "externalEditor"))))
            risks.Add(new("editor.externalEditor", "Run a program to open files", $"External editor: {Str(In("files", "editor", "externalEditor"))}"));
        if (Changed("remote", "xdmcp", "xServerPath") && !string.IsNullOrWhiteSpace(Str(In("remote", "xdmcp", "xServerPath"))))
            risks.Add(new("xdmcp.xServerPath", "Run a program as the X server", $"X server program: {Str(In("remote", "xdmcp", "xServerPath"))}"));
        if (Changed("remote", "xdmcp", "extraArguments") && !string.IsNullOrWhiteSpace(Str(In("remote", "xdmcp", "extraArguments"))))
            risks.Add(new("xdmcp.extraArguments", "Extra X server arguments", Str(In("remote", "xdmcp", "extraArguments"))!));
        if (Changed("logging", "logging", "fileName"))
            risks.Add(new("logging.fileName", "Write session logs to another place", $"Log file: {Str(In("logging", "logging", "fileName"))}"));
        if (In("ssh", "ssh", "manualHostKeys") is JsonArray mk && mk.Count > 0 && Changed("ssh", "ssh", "manualHostKeys"))
            risks.Add(new("ssh.manualHostKeys", "Trust SSH host keys for every server", $"{mk.Count} manually trusted key(s): {string.Join(", ", mk.Select(Str).Take(3))}"));
        if (IsNetworkPath(Str(In("ssh", "ssh", "privateKeyFile"))) && Changed("ssh", "ssh", "privateKeyFile"))
            risks.Add(new("ssh.privateKeyFile", "Read your SSH key from a network share", $"Key file: {Str(In("ssh", "ssh", "privateKeyFile"))} (opening it sends your Windows sign-in to that server)"));
        if ((Bool(In("ssh", "ssh", "localPortsAcceptAll")) && Changed("ssh", "ssh", "localPortsAcceptAll")) ||
            (Bool(In("ssh", "ssh", "remotePortsAcceptAll")) && Changed("ssh", "ssh", "remotePortsAcceptAll")))
            risks.Add(new("ssh.acceptAll", "Let other computers use your tunnels", "Forwarded ports accept connections from other hosts"));
        if (Is(In("files", "files", "certificates"), nameof(CertificatePolicy.AcceptAll)) && Changed("files", "files", "certificates"))
            risks.Add(new("files.certificates", "Accept any TLS certificate", "FTPS connects even when the server's certificate can't be verified"));
        if (Is(In("files", "files", "ftpEncryption"), nameof(FtpEncryption.None)) && Changed("files", "files", "ftpEncryption"))
            risks.Add(new("files.ftpEncryption", "Turn off FTP encryption", "FTP passwords and files are sent in plain text"));
        if (Is(In("remote", "rdp", "serverAuthentication"), nameof(RdpServerAuth.ConnectAnyway)) && Changed("remote", "rdp", "serverAuthentication"))
            risks.Add(new("rdp.serverAuthentication", "Skip Remote Desktop server checks", "Connect even when the server's identity can't be verified"));
        if (In("remote", "rdp", "networkLevelAuthentication") is JsonNode nla && !Bool(nla) && Changed("remote", "rdp", "networkLevelAuthentication"))
            risks.Add(new("rdp.networkLevelAuthentication", "Turn off Network Level Authentication", "Remote Desktop signs in after connecting instead of before"));
        if (Bool(In("terminal", "terminal", "allowRemoteClipboardWrite")) && Changed("terminal", "terminal", "allowRemoteClipboardWrite"))
            risks.Add(new("terminal.allowRemoteClipboardWrite", "Let servers write to your clipboard", "Programs on a server can replace what you paste next"));
        if (In("interface", "interface", "checkForUpdates") is JsonNode cfu && !Bool(cfu) && Changed("interface", "interface", "checkForUpdates"))
            risks.Add(new("interface.checkForUpdates", "Turn off update checks", "FluenTTY would stop telling you about new versions, including security fixes"));
        if (cats["connection"]?["proxy"] is JsonObject proxy && !Is(proxy["type"], nameof(ProxyKind.None)) &&
            (!JsonNode.DeepEquals(proxy["type"], cur["proxy"]?["type"]) || !JsonNode.DeepEquals(proxy["host"], cur["proxy"]?["host"])))
            risks.Add(new("proxy", "Send connections through a proxy", $"{Str(proxy["type"])} proxy {Str(proxy["host"])}:{proxy["port"]}"));
        if (cats["hostkeys"] is JsonArray hk && hk.Count > 0)
            risks.Add(new("hostkeys", "Trust SSH host keys from this file", $"{hk.Count} key(s), e.g. {string.Join(", ", hk.Take(3).Select(k => $"{Str(k?["host"])}:{k?["port"]}"))}"));
        if (cats["sessions"]?["sessions"] is JsonArray ss)
        {
            var unc = ss.Where(s => IsNetworkPath(Str(s?["privateKeyFile"]))).Select(s => Str(s?["name"]) ?? "?").ToList();
            if (unc.Count > 0) risks.Add(new("sessions.networkKeys", "Sessions that read keys from a network share", $"{unc.Count} session(s): {string.Join(", ", unc.Take(5))}"));
        }
        return risks;
    }

    /// <summary>Drops every risky value that wasn't approved (keeps the current value instead).</summary>
    private static JsonObject Neutralize(JsonObject cats, ISet<string> approved, ISet<string> chosen)
    {
        var copy = cats.DeepClone().AsObject();
        var cur = CurrentSettingsNode();
        void Keep(string cat, string section, string prop)
        {
            if (copy[cat]?[section] is JsonObject o && o.ContainsKey(prop)) o[prop] = cur[section]?[prop]?.DeepClone();
        }
        foreach (var r in FindRisks(cats).Where(r => !approved.Contains(r.Id)))
        {
            switch (r.Id)
            {
                case "editor.externalEditor": Keep("files", "editor", "externalEditor"); break;
                case "xdmcp.xServerPath": Keep("remote", "xdmcp", "xServerPath"); break;
                case "xdmcp.extraArguments": Keep("remote", "xdmcp", "extraArguments"); break;
                case "logging.fileName": Keep("logging", "logging", "fileName"); break;
                case "ssh.manualHostKeys": Keep("ssh", "ssh", "manualHostKeys"); break;
                case "ssh.privateKeyFile": Keep("ssh", "ssh", "privateKeyFile"); break;
                case "ssh.acceptAll": Keep("ssh", "ssh", "localPortsAcceptAll"); Keep("ssh", "ssh", "remotePortsAcceptAll"); break;
                case "files.certificates": Keep("files", "files", "certificates"); break;
                case "files.ftpEncryption": Keep("files", "files", "ftpEncryption"); break;
                case "rdp.serverAuthentication": Keep("remote", "rdp", "serverAuthentication"); break;
                case "rdp.networkLevelAuthentication": Keep("remote", "rdp", "networkLevelAuthentication"); break;
                case "terminal.allowRemoteClipboardWrite": Keep("terminal", "terminal", "allowRemoteClipboardWrite"); break;
                case "interface.checkForUpdates": Keep("interface", "interface", "checkForUpdates"); break;
                case "proxy": (copy["connection"] as JsonObject)?.Remove("proxy"); break;
                case "hostkeys": chosen.Remove("hostkeys"); break;
                case "sessions.networkKeys":
                    if (copy["sessions"]?["sessions"] is JsonArray ss)
                        foreach (var s in ss.OfType<JsonObject>().Where(s => IsNetworkPath(Str(s["privateKeyFile"])))) s.Remove("privateKeyFile");
                    break;
            }
        }
        return copy;
    }

    public static void ImportInteractive(Window? owner, string? path = null)
    {
        if (path == null)
        {
            var open = new OpenFileDialog { Filter = FileFilter, Title = "Import settings" };
            if (open.ShowDialog(owner) != true) return;
            path = open.FileName;
        }
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Empty file");
            if (root["format"]?.GetValue<string>() != Format) throw new InvalidDataException("This isn't a FluenTTY settings file.");
        }
        catch (Exception ex)
        {
            FluentDialog.Info(owner, "Can't import", ex.Message);
            return;
        }
        var cats = root["categories"] as JsonObject ?? new JsonObject();
        bool hasPasswords = root["passwordsIncluded"]?.GetValue<bool>() == true;

        var panel = new StackPanel();
        var intro = new TextBlock
        {
            Text = $"Exported {root["exported"]?.GetValue<string>()?.Replace('T', ' ')} from FluenTTY {root["app"]?.GetValue<string>()}. Choose what to bring in — each chosen category replaces your current one (sessions and host keys are merged).",
            TextWrapping = TextWrapping.Wrap,
        };
        intro.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        panel.Children.Add(intro);
        var boxes = new Dictionary<string, CheckBox>();
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in Categories.Where(c => cats.ContainsKey(c.Key)))
        {
            string extra = c.Key == "sessions" && cats["sessions"]?["sessions"] is JsonArray arr ? $" ({arr.Count})" : "";
            var cb = new CheckBox { Content = c.Title + extra, IsChecked = true, ToolTip = c.Description, Margin = new Thickness(0, 4, 8, 4) };
            boxes[c.Key] = cb;
            grid.Children.Add(cb);
        }
        panel.Children.Add(grid);
        var replaceSessions = new CheckBox { Content = "Replace sessions that already exist (matched by ID)", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        if (cats.ContainsKey("sessions")) panel.Children.Add(replaceSessions);

        var risks = FindRisks(cats);
        var riskBoxes = new Dictionary<string, CheckBox>();
        if (risks.Count > 0)
        {
            var warn = new Border { Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(4) };
            warn.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
            var wp = new StackPanel();
            var wt = new TextBlock
            {
                Text = "This file also wants to change security settings. Only tick what you expected from whoever sent you the file — unticked items keep your current setting.",
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
            };
            wp.Children.Add(wt);
            foreach (var r in risks)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = r.Title, TextWrapping = TextWrapping.Wrap });
                var d = new TextBlock { Text = r.Detail, TextWrapping = TextWrapping.Wrap };
                d.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
                content.Children.Add(d);
                var cb = new CheckBox { Content = content, IsChecked = false, Margin = new Thickness(0, 8, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(cb, "Allow: " + r.Title);
                riskBoxes[r.Id] = cb;
                wp.Children.Add(cb);
            }
            warn.Child = wp;
            panel.Children.Add(warn);
        }
        PasswordBox? pass = null;
        if (hasPasswords)
        {
            var label = new TextBlock { Text = "This file contains passwords. Enter its passphrase to import them (leave empty to skip passwords):", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
            label.SetResourceReference(FrameworkElement.StyleProperty, "Body");
            pass = new PasswordBox();
            System.Windows.Automation.AutomationProperties.SetName(pass, "Passphrase");
            panel.Children.Add(label);
            panel.Children.Add(pass);
        }
        var dlg = new FluentDialog(owner, "Import settings", panel, "Import") { Width = 580 };
        string? passphrase = null;
        dlg.Validate = () =>
        {
            passphrase = pass?.Password;
            if (string.IsNullOrEmpty(passphrase)) { passphrase = null; return true; }
            if (!CheckPassphrase(cats, passphrase))
            {
                FluentDialog.Info(dlg, "Wrong passphrase", "That passphrase doesn't unlock the passwords in this file.");
                return false;
            }
            return true;
        };
        if (dlg.ShowDialog() != DialogResultKind.Primary) return;

        var chosen = boxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        var approved = riskBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        var summary = Import(cats, chosen, passphrase, replaceSessions.IsChecked == true, approved);
        FluentDialog.Info(owner, "Import finished", summary);
    }

    private static bool CheckPassphrase(JsonObject cats, string passphrase)
    {
        var sample = cats["sessions"]?["sessions"]?.AsArray().Select(s => s?["password"]?.GetValue<string>()).FirstOrDefault(p => p != null)
                     ?? cats["connection"]?["proxy"]?["password"]?.GetValue<string>();
        return sample == null || !sample.StartsWith("aesgcm:") || SecretStore.DecryptFromExport(sample, passphrase) != null;
    }

    /// <param name="approvedRisks">Ids from <see cref="FindRisks"/> the user ticked; everything else risky is dropped.</param>
    public static string Import(JsonObject cats, ISet<string> chosen, string? passphrase, bool replaceSessions, ISet<string>? approvedRisks = null)
    {
        chosen = new HashSet<string>(chosen);
        var risks = FindRisks(cats);
        var approved = approvedRisks ?? new HashSet<string>();
        cats = Neutralize(cats, approved, chosen);
        var lines = new List<string>();
        int held = risks.Count(r => !approved.Contains(r.Id));
        var settingsNode = JsonSerializer.SerializeToNode(SettingsStore.Settings, Json.Options)!.AsObject();
        bool settingsChanged = false;

        foreach (var c in Categories.Where(c => chosen.Contains(c.Key) && cats.ContainsKey(c.Key)))
        {
            switch (c.Key)
            {
                case "sessions":
                {
                    var arr = cats["sessions"]?["sessions"]?.Deserialize<List<SessionProfile>>(Json.Options) ?? new();
                    var folders = cats["sessions"]?["folders"]?.Deserialize<List<string>>(Json.Options) ?? new();
                    int added = 0, replaced = 0, skipped = 0;
                    foreach (var s in arr)
                    {
                        s.Password = s.Password != null && passphrase != null ? SecretStore.Protect(SecretStore.DecryptFromExport(s.Password, passphrase)) : null;
                        int i = SettingsStore.Sessions.Sessions.FindIndex(x => x.Id == s.Id);
                        if (i >= 0)
                        {
                            if (replaceSessions) { SettingsStore.Sessions.Sessions[i] = s; replaced++; }
                            else skipped++;
                        }
                        else { SettingsStore.Sessions.Sessions.Add(s); added++; }
                    }
                    foreach (var f in folders) if (!SettingsStore.Sessions.Folders.Contains(f)) SettingsStore.Sessions.Folders.Add(f);
                    SettingsStore.SaveSessions();
                    lines.Add($"Sessions: {added} added, {replaced} replaced{(skipped > 0 ? $", {skipped} kept as they were" : "")}.");
                    break;
                }
                case "hostkeys":
                {
                    var keys = cats["hostkeys"]?.Deserialize<List<KnownHostKey>>(Json.Options) ?? new();
                    foreach (var k in keys) SettingsStore.HostKeys.RemoveAll(x => x.Key.Equals(k.Key, StringComparison.OrdinalIgnoreCase));
                    SettingsStore.HostKeys.AddRange(keys);
                    SettingsStore.SaveHostKeys();
                    lines.Add($"Known host keys: {keys.Count} imported.");
                    break;
                }
                default:
                    if (cats[c.Key] is not JsonObject obj) break;
                    foreach (var section in c.Sections)
                    {
                        if (obj[section] is not JsonNode node) continue;
                        var copy = node.DeepClone();
                        if (section == "proxy" && copy is JsonObject proxy)
                        {
                            var pw = proxy["password"]?.GetValue<string>();
                            proxy["password"] = pw != null && pw.StartsWith("aesgcm:") && passphrase != null
                                ? SecretStore.Protect(SecretStore.DecryptFromExport(pw, passphrase)) ?? ""
                                : "";
                        }
                        settingsNode[section] = copy;
                    }
                    settingsChanged = true;
                    lines.Add($"{c.Title}: imported.");
                    break;
            }
        }
        if (settingsChanged)
        {
            var next = settingsNode.Deserialize<AppSettings>(Json.Options) ?? new AppSettings();
            next.Interface.OnboardingDone = true;
            SettingsStore.ReplaceSettings(next);
        }
        if (held > 0) lines.Add($"{held} security-sensitive change(s) were not applied; your current settings were kept.");
        return lines.Count == 0 ? "Nothing was imported." : string.Join("\n", lines);
    }
}
