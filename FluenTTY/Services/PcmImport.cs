using System.Text;
using System.Xml.Linq;
using FluenTTY.Models;

namespace FluenTTY.Services;

/// <summary>
/// Converts a PuTTY Connection Manager database (.dat / exported .xml) into FluenTTY sessions.
///
/// PuTTY CM's unencrypted database is XML:
/// <code>
/// &lt;configuration version=".." savepassword="True"&gt;
///   &lt;root type="database" name="Department"&gt;
///     &lt;container type="folder" name="Switches"&gt;
///       &lt;connection type="PuTTY" name="core-sw1"&gt;
///         &lt;connection_info&gt;&lt;name/&gt;&lt;protocol&gt;SSH&lt;/protocol&gt;&lt;host/&gt;&lt;port/&gt;&lt;session&gt;Default Settings&lt;/session&gt;&lt;commandline/&gt;&lt;description/&gt;&lt;/connection_info&gt;
///         &lt;login&gt;&lt;login&gt;user&lt;/login&gt;&lt;password/&gt;&lt;prompt/&gt;&lt;/login&gt;
///         &lt;command&gt;&lt;command1/&gt;…&lt;command5/&gt;&lt;/command&gt;
///         &lt;options&gt;&lt;loginmacro&gt;False&lt;/loginmacro&gt;&lt;postcommands&gt;False&lt;/postcommands&gt;&lt;endlinechar&gt;10&lt;/endlinechar&gt;&lt;ssh_public_key/&gt;&lt;/options&gt;
///       &lt;/connection&gt;
/// </code>
/// (schema as read by github.com/cfstras/pcm). Folders become sidebar folders under the database
/// name; "login macro" becomes a login script; post-login commands become the initial command; the
/// PuTTY saved session a connection points at is merged in when it exists on this PC.
/// An AES-encrypted database isn't XML and can't be read — export it unencrypted from PuTTY CM first.
/// </summary>
public static class PcmImport
{
    public sealed class Result
    {
        public List<SessionProfile> Sessions { get; } = new();
        public List<string> Warnings { get; } = new();
        public string DatabaseName { get; set; } = "";
        public int WithPasswords { get; set; }
    }

    public static Result Read(string path, bool includePasswords)
    {
        var bytes = File.ReadAllBytes(path);
        var text = DecodeText(bytes);
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith('<'))
            throw new InvalidDataException("This PuTTY Connection Manager database is encrypted. In PuTTY CM, open it and use File › Export (or turn off database encryption), then import the exported file.");

        var doc = XDocument.Parse(trimmed);
        var config = doc.Root ?? throw new InvalidDataException("The file is empty.");
        if (config.Name.LocalName != "configuration")
            throw new InvalidDataException($"This doesn't look like a PuTTY Connection Manager database (root element <{config.Name.LocalName}>).");

        var result = new Result();
        var root = config.Element("root") ?? config;
        result.DatabaseName = Attr(root, "name");
        var baseFolder = string.IsNullOrWhiteSpace(result.DatabaseName) ? "PuTTY CM" : Clean(result.DatabaseName);
        Walk(root, baseFolder, result, includePasswords);
        return result;
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Walk(XElement node, string folder, Result result, bool includePasswords)
    {
        foreach (var child in node.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "container":
                    var name = Clean(Attr(child, "name"));
                    Walk(child, string.IsNullOrEmpty(name) ? folder : $"{folder}/{name}", result, includePasswords);
                    break;
                case "connection":
                    try
                    {
                        var p = Convert(child, folder, result, includePasswords);
                        if (p != null) result.Sessions.Add(p);
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Skipped \"{Attr(child, "name")}\": {ex.Message}");
                    }
                    break;
            }
        }
    }

    private static SessionProfile? Convert(XElement c, string folder, Result result, bool includePasswords)
    {
        var info = c.Element("connection_info");
        var login = c.Element("login");
        var options = c.Element("options");
        var commands = c.Element("command");

        string name = Attr(c, "name");
        if (string.IsNullOrWhiteSpace(name)) name = Val(info, "name");
        string host = Val(info, "host").Trim();
        string protoText = Val(info, "protocol").Trim().ToUpperInvariant();

        var protocol = protoText switch
        {
            "SSH" or "SSH2" or "SSH1" or "" => ProtocolKind.Ssh,
            "TELNET" => ProtocolKind.Telnet,
            "RLOGIN" => ProtocolKind.Rlogin,
            "RAW" => ProtocolKind.Raw,
            "SERIAL" => ProtocolKind.Serial,
            _ => (ProtocolKind?)null,
        };
        if (protocol == null)
        {
            result.Warnings.Add($"Skipped \"{name}\": protocol \"{protoText}\" isn't supported.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(host) && protocol != ProtocolKind.Serial)
        {
            result.Warnings.Add($"Skipped \"{name}\": no host name.");
            return null;
        }

        var p = new SessionProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? host : name.Trim(),
            Folder = folder,
            Protocol = protocol.Value,
            Host = host,
            Username = Val(login, "login").Trim(),
            Notes = Val(info, "description").Trim(),
        };
        if (int.TryParse(Val(info, "port"), out int port) && port > 0 && port != SessionProfile.DefaultPort(p.Protocol))
            p.Port = port;

        // A PuTTY saved session named in the connection supplies defaults (key file, terminal type...).
        var sessionName = Val(info, "session").Trim();
        if (sessionName.Length > 0 && sessionName != "Default Settings")
        {
            var putty = PuttyImport.ReadSessions().FirstOrDefault(s => s.Name == sessionName);
            if (putty != null)
            {
                if (string.IsNullOrEmpty(p.Username)) p.Username = putty.Username;
                if (string.IsNullOrEmpty(p.PrivateKeyFile)) p.PrivateKeyFile = putty.PrivateKeyFile;
                p.TerminalType ??= putty.TerminalType;
                p.RemoteCharset ??= putty.RemoteCharset;
                p.KeepaliveSeconds ??= putty.KeepaliveSeconds;
                if (p.PortForwards.Count == 0) p.PortForwards.AddRange(putty.PortForwards);
                if (p.Protocol == ProtocolKind.Serial)
                {
                    p.SerialSpeed = putty.SerialSpeed; p.SerialDataBits = putty.SerialDataBits; p.SerialParity = putty.SerialParity;
                    p.SerialStopBits = putty.SerialStopBits; p.SerialFlow = putty.SerialFlow;
                    if (string.IsNullOrEmpty(p.Host)) p.Host = putty.Host;
                }
            }
            else
            {
                result.Warnings.Add($"\"{p.Name}\" uses the PuTTY session \"{sessionName}\", which isn't saved in PuTTY on this PC — its settings weren't merged.");
            }
        }

        var key = Val(options, "ssh_public_key").Trim();
        if (key.Length > 0) p.PrivateKeyFile = key;

        ApplyCommandLine(p, Val(info, "commandline"));

        // A shared database must not point key files at a network share: Windows would send the
        // user's sign-in (NTLM) to that server the moment FluenTTY checks the file.
        // Security review M6 — see CLAUDE.md "Security invariants".
        if (SettingsTransfer.IsNetworkPath(p.PrivateKeyFile))
        {
            result.Warnings.Add($"\"{p.Name}\": the key file {p.PrivateKeyFile} is on a network share and wasn't imported. Copy the key to this PC and set it in the session.");
            p.PrivateKeyFile = "";
        }

        var password = Val(login, "password");
        if (includePasswords && password.Length > 0)
        {
            p.Password = SecretStore.Protect(password);
            result.WithPasswords++;
        }

        // Login macro: PuTTY CM types the user name and password at the prompts (mostly for Telnet devices).
        if (Bool(options, "loginmacro") && p.Protocol != ProtocolKind.Ssh)
        {
            p.LoginScript = LoginStep.UsernamePassword();
            var prompt = Val(login, "prompt").Trim();
            if (prompt.Length > 0) p.LoginScript[0].Expect = System.Text.RegularExpressions.Regex.Escape(prompt);
        }

        var cmds = Enumerable.Range(1, 5).Select(i => Val(commands, $"command{i}")).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (cmds.Count > 0) p.InitialCommand = string.Join("\n", cmds);
        return p;
    }

    /// <summary>PuTTY-style switches that PuTTY CM passes through (-i, -P, -l, -L/-R/-D, -C, -A ignored).</summary>
    private static void ApplyCommandLine(SessionProfile p, string cmdline)
    {
        if (string.IsNullOrWhiteSpace(cmdline)) return;
        var args = SplitArgs(cmdline);
        for (int i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : "";
            switch (args[i])
            {
                case "-i": p.PrivateKeyFile = Next(); break;
                case "-P": if (int.TryParse(Next(), out int port)) p.Port = port; break;
                case "-l": p.Username = Next(); break;
                case "-C": p.Compression = true; break;
                case "-L": case "-R": case "-D":
                {
                    var kind = args[i][1];
                    var spec = Next();
                    var pf = PuttyImport.ParseForward(ToStoredForward(kind, spec));
                    if (pf != null) p.PortForwards.Add(pf);
                    break;
                }
            }
        }
    }

    /// <summary>PuTTY command-line form ([bind:]port:host:port) → saved-session form ([bind:]port=host:port).</summary>
    private static string ToStoredForward(char kind, string spec)
    {
        if (kind == 'D') return "D" + spec;
        var parts = spec.Split(':');
        return parts.Length switch
        {
            3 => $"{kind}{parts[0]}={parts[1]}:{parts[2]}",
            4 => $"{kind}{parts[0]}:{parts[1]}={parts[2]}:{parts[3]}",
            _ => $"{kind}{spec}",
        };
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (var ch in s)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? "";
    private static string Val(XElement? parent, string name) => parent?.Element(name)?.Value ?? "";
    private static bool Bool(XElement? parent, string name) => bool.TryParse(Val(parent, name), out var b) && b;
    private static string Clean(string folder) => folder.Replace('/', '-').Replace('\\', '-').Trim();

    /// <summary>Adds the converted sessions; returns (added, replaced, skipped).</summary>
    public static (int added, int replaced, int skipped) Commit(IEnumerable<SessionProfile> sessions, bool replaceExisting)
    {
        int added = 0, replaced = 0, skipped = 0;
        foreach (var p in sessions)
        {
            var existing = SettingsStore.Sessions.Sessions.FirstOrDefault(s => s.Name == p.Name && s.Folder == p.Folder);
            if (existing != null)
            {
                if (!replaceExisting) { skipped++; continue; }
                p.Id = existing.Id;
                SettingsStore.Sessions.Sessions[SettingsStore.Sessions.Sessions.IndexOf(existing)] = p;
                replaced++;
            }
            else
            {
                SettingsStore.Sessions.Sessions.Add(p);
                added++;
            }
        }
        SettingsStore.SaveSessions();
        return (added, replaced, skipped);
    }
}

