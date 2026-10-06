using FluenTTY.Models;

namespace FluenTTY.Services;

/// <summary>
/// PuTTY-compatible command line: <c>-ssh/-telnet/-rlogin/-raw/-serial</c>, <c>-P port</c>, <c>-l user</c>,
/// <c>-pw password</c>, <c>-i keyfile</c>, <c>-load "saved session"</c>, <c>-sercfg 9600,8,n,1,N</c>,
/// <c>[user@]host</c>; plus URLs such as <c>ssh://user@host:2222</c>, <c>sftp://</c>, <c>ftp://</c>,
/// <c>telnet://</c>, <c>rdp://</c>, <c>vnc://</c>.
/// </summary>
public static class CommandLine
{
    public static List<SessionProfile> Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var result = new List<SessionProfile>();
        ProtocolKind? protocol = null;
        int port = 0;
        string? user = null, password = null, key = null, host = null, sercfg = null;

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : "";
            switch (a.ToLowerInvariant())
            {
                case "-ssh": protocol = ProtocolKind.Ssh; break;
                case "-telnet": protocol = ProtocolKind.Telnet; break;
                case "-rlogin": protocol = ProtocolKind.Rlogin; break;
                case "-raw": protocol = ProtocolKind.Raw; break;
                case "-serial": protocol = ProtocolKind.Serial; break;
                case "-sftp": protocol = ProtocolKind.Sftp; break;
                case "-ftp": protocol = ProtocolKind.Ftp; break;
                case "-rdp": protocol = ProtocolKind.Rdp; break;
                case "-vnc": protocol = ProtocolKind.Vnc; break;
                case "-p": port = int.TryParse(Next(), out var p) ? p : 0; break;
                case "-l": user = Next(); break;
                case "-pw": password = Next(); break;
                case "-i": key = Next(); break;
                case "-sercfg": sercfg = Next(); break;
                case "-load":
                {
                    var name = Next();
                    var s = SettingsStore.Sessions.Sessions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (s != null) result.Add(s.Clone());
                    else error = $"No saved session called \"{name}\".";
                    break;
                }
                default:
                    if (a.StartsWith('-')) break; // unknown switches are ignored, like PuTTY's GUI does for most
                    if (TryParseUrl(a, out var fromUrl)) { result.Add(fromUrl!); break; }
                    host = a;
                    break;
            }
        }

        if (host != null)
        {
            var prof = QuickConnect.Parse(host, protocol ?? ProtocolKind.Ssh) ?? new SessionProfile { Host = host, Protocol = protocol ?? ProtocolKind.Ssh };
            if (port > 0) prof.Port = port;
            if (user != null) prof.Username = user;
            if (password != null) prof.Password = SecretStore.Protect(password);
            if (key != null) prof.PrivateKeyFile = key;
            if (sercfg != null && prof.Protocol == ProtocolKind.Serial) ApplySercfg(prof, sercfg);
            result.Add(prof);
        }
        else if (result.Count > 0)
        {
            foreach (var r in result)
            {
                if (port > 0) r.Port = port;
                if (user != null) r.Username = user;
                if (password != null) r.Password = SecretStore.Protect(password);
                if (key != null) r.PrivateKeyFile = key;
            }
        }
        return result;
    }

    public static bool TryParseUrl(string text, out SessionProfile? profile)
    {
        profile = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        ProtocolKind? kind = uri.Scheme.ToLowerInvariant() switch
        {
            "ssh" => ProtocolKind.Ssh,
            "sftp" => ProtocolKind.Sftp,
            "ftp" or "ftps" => ProtocolKind.Ftp,
            "telnet" => ProtocolKind.Telnet,
            "rlogin" => ProtocolKind.Rlogin,
            "rdp" => ProtocolKind.Rdp,
            "vnc" => ProtocolKind.Vnc,
            _ => null,
        };
        if (kind == null) return false;
        profile = new SessionProfile
        {
            Protocol = kind.Value,
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 0 : uri.Port,
            Username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]),
        };
        if (uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase)) profile.FtpEncryption = FtpEncryption.Implicit;
        return true;
    }

    /// <summary>PuTTY -sercfg: speed,data bits,parity (n/o/e/m/s),stop bits (1/1.5/2),flow (N/X/R/D).</summary>
    public static void ApplySercfg(SessionProfile p, string cfg)
    {
        foreach (var part in cfg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int n))
            {
                if (n >= 5 && n <= 9) p.SerialDataBits = n;
                else if (n == 1) p.SerialStopBits = SerialStopBits.One;
                else if (n == 2) p.SerialStopBits = SerialStopBits.Two;
                else p.SerialSpeed = n;
                continue;
            }
            switch (part)
            {
                case "1.5": p.SerialStopBits = SerialStopBits.OnePointFive; break;
                case "n": p.SerialParity = SerialParity.None; break;
                case "o": p.SerialParity = SerialParity.Odd; break;
                case "e": p.SerialParity = SerialParity.Even; break;
                case "m": p.SerialParity = SerialParity.Mark; break;
                case "s": p.SerialParity = SerialParity.Space; break;
                case "N": p.SerialFlow = SerialFlow.None; break;
                case "X": p.SerialFlow = SerialFlow.XonXoff; break;
                case "R": p.SerialFlow = SerialFlow.RtsCts; break;
                case "D": p.SerialFlow = SerialFlow.DsrDtr; break;
            }
        }
    }
}

/// <summary>Parses what people type into the quick-connect box ("user@host:port", "COM3", URLs).</summary>
public static class QuickConnect
{
    public static SessionProfile? Parse(string text, ProtocolKind protocol)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (CommandLine.TryParseUrl(text, out var url)) return url;

        if (protocol == ProtocolKind.Serial || System.Text.RegularExpressions.Regex.IsMatch(text, @"^COM\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new SessionProfile { Protocol = ProtocolKind.Serial, Host = text.ToUpperInvariant() };

        var p = new SessionProfile { Protocol = protocol };
        int at = text.LastIndexOf('@');
        if (at > 0)
        {
            p.Username = text[..at];
            text = text[(at + 1)..];
        }
        // [v6addr]:port, host:port, bare IPv6
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close > 0)
            {
                p.Host = text[1..close];
                if (close + 2 < text.Length && text[close + 1] == ':' && int.TryParse(text[(close + 2)..], out int v6port)) p.Port = v6port;
                return p;
            }
        }
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port))
        {
            p.Host = text[..colon];
            p.Port = port;
        }
        else p.Host = text;
        return string.IsNullOrWhiteSpace(p.Host) ? null : p;
    }
}
