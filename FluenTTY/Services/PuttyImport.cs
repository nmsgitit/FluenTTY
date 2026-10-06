using FluenTTY.Models;
using Microsoft.Win32;

namespace FluenTTY.Services;

/// <summary>
/// Reads PuTTY's saved sessions (HKCU\Software\SimonTatham\PuTTY\Sessions, the format written by
/// PuTTY's windows/storage.c + settings.c) into FluenTTY sessions under a "PuTTY" folder. Read-only:
/// PuTTY's registry data is never modified.
/// </summary>
public static class PuttyImport
{
    private const string SessionsKey = @"Software\SimonTatham\PuTTY\Sessions";

    public static List<string> SessionNames()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SessionsKey);
            if (key == null) return new List<string>();
            return key.GetSubKeyNames().Select(Unescape).Where(n => n != "Default Settings").ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>PuTTY stores session names %XX-escaped.</summary>
    private static string Unescape(string s) => Uri.UnescapeDataString(s);

    public static List<SessionProfile> ReadSessions()
    {
        var list = new List<SessionProfile>();
        using var root = Registry.CurrentUser.OpenSubKey(SessionsKey);
        if (root == null) return list;
        foreach (var sub in root.GetSubKeyNames())
        {
            var name = Unescape(sub);
            if (name == "Default Settings") continue;
            using var k = root.OpenSubKey(sub);
            if (k == null) continue;
            var p = Read(k, name);
            if (p != null) list.Add(p);
        }
        return list;
    }

    private static SessionProfile? Read(RegistryKey k, string name)
    {
        string Str(string v, string def = "") => k.GetValue(v) as string ?? def;
        int Int(string v, int def) => k.GetValue(v) is int i ? i : def;

        var protocol = Str("Protocol", "ssh").ToLowerInvariant() switch
        {
            "telnet" => ProtocolKind.Telnet,
            "rlogin" => ProtocolKind.Rlogin,
            "raw" => ProtocolKind.Raw,
            "serial" => ProtocolKind.Serial,
            "ssh" => ProtocolKind.Ssh,
            _ => (ProtocolKind?)null,
        };
        if (protocol == null) return null; // SUPDUP and anything unknown

        var p = new SessionProfile
        {
            Name = name,
            Folder = "PuTTY",
            Protocol = protocol.Value,
            Host = protocol == ProtocolKind.Serial ? Str("SerialLine", "COM1") : Str("HostName"),
            Username = Str("UserName"),
            PrivateKeyFile = Str("PublicKeyFile"),
            Notes = "Imported from PuTTY",
        };
        int port = Int("PortNumber", 0);
        if (port > 0 && port != SessionProfile.DefaultPort(p.Protocol)) p.Port = port;

        // "user@host" in the host field works in PuTTY too.
        if (p.Protocol != ProtocolKind.Serial && p.Host.Contains('@') && string.IsNullOrEmpty(p.Username))
        {
            int at = p.Host.LastIndexOf('@');
            p.Username = p.Host[..at];
            p.Host = p.Host[(at + 1)..];
        }

        var term = Str("TerminalType");
        if (term.Length > 0 && term != "xterm") p.TerminalType = term;
        var cp = Str("LineCodePage");
        if (cp.Length > 0 && !cp.StartsWith("UTF-8", StringComparison.OrdinalIgnoreCase) && !cp.StartsWith("Use font", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = MapCodePage(cp);
            if (mapped != null) p.RemoteCharset = mapped;
        }
        var cmd = Str("RemoteCommand");
        if (cmd.Length > 0) p.InitialCommand = cmd;
        int ping = Int("PingIntervalSecs", 0) + Int("PingInterval", 0) * 60;
        if (ping > 0) p.KeepaliveSeconds = ping;
        if (Int("Compression", 0) != 0) p.Compression = true;
        if (Int("ProxyMethod", 0) == 0) p.UseProxy = false;
        int logType = Int("LogType", 0);
        if (logType == 1) p.LogMode = LogMode.Printable;
        else if (logType >= 2) p.LogMode = LogMode.AllOutput;

        if (p.Protocol == ProtocolKind.Serial)
        {
            p.SerialSpeed = Int("SerialSpeed", 9600);
            p.SerialDataBits = Int("SerialDataBits", 8);
            p.SerialStopBits = Int("SerialStopHalfbits", 2) switch { 3 => SerialStopBits.OnePointFive, 4 => SerialStopBits.Two, _ => SerialStopBits.One };
            p.SerialParity = Int("SerialParity", 0) switch { 1 => SerialParity.Odd, 2 => SerialParity.Even, 3 => SerialParity.Mark, 4 => SerialParity.Space, _ => SerialParity.None };
            p.SerialFlow = Int("SerialFlowControl", 1) switch { 0 => SerialFlow.None, 2 => SerialFlow.RtsCts, 3 => SerialFlow.DsrDtr, _ => SerialFlow.XonXoff };
        }

        // PortForwardings: "L8080=localhost:80,R2222=host:22,D1080"
        foreach (var f in Str("PortForwardings").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pf = ParseForward(f.Trim());
            if (pf != null) p.PortForwards.Add(pf);
        }
        return p;
    }

    public static PortForward? ParseForward(string spec)
    {
        if (spec.Length < 2) return null;
        // Optional address-family prefix (4 or 6) before the type letter.
        if (spec[0] is '4' or '6') spec = spec[1..];
        char type = char.ToUpperInvariant(spec[0]);
        var rest = spec[1..];
        string source, dest = "";
        int eq = rest.IndexOf('=');
        if (eq >= 0) { source = rest[..eq]; dest = rest[(eq + 1)..]; }
        else source = rest;
        var pf = new PortForward { Kind = type switch { 'R' => PortForwardKind.Remote, 'D' => PortForwardKind.Dynamic, _ => PortForwardKind.Local } };
        int sc = source.LastIndexOf(':');
        if (sc > 0) { pf.BindAddress = source[..sc]; source = source[(sc + 1)..]; }
        if (!int.TryParse(source, out int sp)) return null;
        pf.SourcePort = sp;
        if (dest == "D" || type == 'D') { pf.Kind = PortForwardKind.Dynamic; return pf; }
        int dc = dest.LastIndexOf(':');
        if (dc <= 0 || !int.TryParse(dest[(dc + 1)..], out int dp)) return null;
        pf.DestinationHost = dest[..dc];
        pf.DestinationPort = dp;
        return pf;
    }

    private static string? MapCodePage(string putty)
    {
        var s = putty.ToUpperInvariant();
        if (s.StartsWith("ISO-8859-")) return s.Split(' ')[0];
        if (s.StartsWith("KOI8-U")) return "KOI8-U";
        if (s.StartsWith("KOI8")) return "KOI8-R";
        if (s.StartsWith("CP") && int.TryParse(new string(s.Skip(2).TakeWhile(char.IsDigit).ToArray()), out int cp))
            return cp switch { >= 1250 and <= 1258 => $"Windows-{cp}", 437 or 850 or 852 or 866 => $"IBM{cp}", _ => null };
        if (s.StartsWith("WIN")) return null;
        return null;
    }

    public static bool HasDefaultSettings()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SessionsKey + @"\Default%20Settings");
            return k != null && k.ValueCount > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Copies PuTTY's "Default Settings" (font, colours, terminal, keyboard, bell, selection, connection
    /// options) into FluenTTY's settings. Values PuTTY doesn't store are left alone. Returns a short
    /// description of what was imported. Stored enum values follow PuTTY's conf-enums.h.
    /// </summary>
    public static List<string> ImportDefaultSettings(AppSettings s)
    {
        var done = new List<string>();
        using var k = Registry.CurrentUser.OpenSubKey(SessionsKey + @"\Default%20Settings");
        if (k == null) return done;
        bool Has(string v) => k.GetValue(v) != null;
        string Str(string v, string def = "") => k.GetValue(v) as string ?? def;
        int Int(string v, int def) => k.GetValue(v) is int i ? i : def;
        bool Bool(string v, bool def) => k.GetValue(v) is int i ? i != 0 : def;
        AutoOnOff Tri(string v, AutoOnOff def) => k.GetValue(v) is int i ? i switch { 0 => AutoOnOff.On, 1 => AutoOnOff.Off, _ => AutoOnOff.Auto } : def;

        // Window › Appearance
        if (Has("Font"))
        {
            var font = Str("Font");
            if (font.Length > 0 && !font.Equals("Courier New", StringComparison.OrdinalIgnoreCase)) s.Appearance.FontFamily = font;
            s.Appearance.FontSize = Math.Clamp(Int("FontHeight", 10), 6, 48);
            s.Appearance.FontQuality = Int("FontQuality", 0) switch { 1 => FontQuality.Antialiased, 2 => FontQuality.NonAntialiased, 3 => FontQuality.ClearType, _ => FontQuality.Default };
            done.Add($"Font: {s.Appearance.FontFamily} {s.Appearance.FontSize} pt");
        }
        if (Has("CurType"))
        {
            s.Appearance.CursorShape = Int("CurType", 0) switch { 1 => CursorShape.Underline, 2 => CursorShape.Bar, _ => CursorShape.Block };
            s.Appearance.CursorBlink = Bool("BlinkCur", false);
        }
        if (Has("BoldAsColour")) s.Appearance.BoldStyle = Int("BoldAsColour", 2) switch { 0 => BoldStyle.Font, 1 => BoldStyle.Colour, _ => BoldStyle.Both };
        s.Appearance.AnsiColour = Bool("ANSIColour", s.Appearance.AnsiColour);
        s.Appearance.Xterm256Colour = Bool("Xterm256Colour", s.Appearance.Xterm256Colour);
        s.Appearance.TrueColour = Bool("TrueColour", s.Appearance.TrueColour);
        s.Appearance.ShowScrollbar = Bool("ScrollBar", s.Appearance.ShowScrollbar);
        s.Appearance.HideMouseWhileTyping = Bool("HideMousePtr", s.Appearance.HideMouseWhileTyping);

        // Window › Colours → a custom scheme, used for both light and dark mode (PuTTY has one palette)
        if (Has("Colour0"))
        {
            string C(int i)
            {
                var parts = Str($"Colour{i}").Split(',');
                return parts.Length == 3 && parts.All(p => int.TryParse(p, out _))
                    ? $"#{int.Parse(parts[0]):X2}{int.Parse(parts[1]):X2}{int.Parse(parts[2]):X2}"
                    : "#000000";
            }
            var scheme = new ColorScheme
            {
                Name = "PuTTY (imported)",
                Foreground = C(0), BoldForeground = C(1), Background = C(2), CursorText = C(4), Cursor = C(5), Selection = C(0),
                Ansi = new[] { C(6), C(8), C(10), C(12), C(14), C(16), C(18), C(20), C(7), C(9), C(11), C(13), C(15), C(17), C(19), C(21) },
            };
            s.Appearance.CustomSchemes.RemoveAll(x => x.Name == scheme.Name);
            s.Appearance.CustomSchemes.Add(scheme);
            s.Appearance.SchemeDark = scheme.Name;
            s.Appearance.SchemeLight = scheme.Name;
            done.Add("Colours (as the \"PuTTY (imported)\" scheme)");
        }

        // Terminal
        var t = s.Terminal;
        t.AutoWrap = Bool("AutoWrapMode", t.AutoWrap);
        t.DecOriginMode = Bool("DECOriginMode", t.DecOriginMode);
        t.LfImpliesCr = Bool("LFImpliesCR", t.LfImpliesCr);
        t.CrImpliesLf = Bool("CRImpliesLF", t.CrImpliesLf);
        t.BackgroundColourErase = Bool("BCE", t.BackgroundColourErase);
        t.BlinkText = Bool("BlinkText", t.BlinkText);
        if (Has("Answerback")) t.Answerback = Str("Answerback") == "PuTTY" ? "FluenTTY" : Str("Answerback");
        t.LocalEcho = Tri("LocalEcho", t.LocalEcho);
        t.LocalLineEditing = Tri("LocalEdit", t.LocalLineEditing);
        if (Has("ScrollbackLines")) { t.ScrollbackLines = Math.Max(t.ScrollbackLines, Int("ScrollbackLines", 2000)); done.Add($"Scrollback: {t.ScrollbackLines} lines"); }
        t.EraseToScrollback = Bool("EraseToScrollback", t.EraseToScrollback);
        t.ScrollOnKeypress = Bool("ScrollOnKey", t.ScrollOnKeypress);
        t.ScrollOnOutput = Bool("ScrollOnDisp", t.ScrollOnOutput);
        t.NoApplicationCursorKeys = Bool("NoApplicationCursors", t.NoApplicationCursorKeys);
        t.NoApplicationKeypad = Bool("NoApplicationKeys", t.NoApplicationKeypad);
        t.NoMouseReporting = Bool("NoMouseReporting", t.NoMouseReporting);
        t.NoRemoteResize = Bool("NoRemoteResize", t.NoRemoteResize);
        t.NoAltScreen = Bool("NoAltScreen", t.NoAltScreen);
        t.NoRemoteWindowTitle = Bool("NoRemoteWinTitle", t.NoRemoteWindowTitle);
        t.NoRemoteClearScrollback = Bool("NoRemoteClearScroll", t.NoRemoteClearScrollback);
        t.NoDestructiveBackspace = Bool("NoDBackspace", t.NoDestructiveBackspace);
        t.NoRemoteCharset = Bool("NoRemoteCharset", t.NoRemoteCharset);
        t.NoBracketedPaste = Bool("DisableBracketedPaste", t.NoBracketedPaste);

        // Keyboard
        var kb = s.Keyboard;
        if (Has("BackspaceIsDelete")) kb.Backspace = Bool("BackspaceIsDelete", true) ? BackspaceKey.ControlQuestion : BackspaceKey.ControlH;
        if (Has("RXVTHomeEnd")) kb.HomeEnd = Bool("RXVTHomeEnd", false) ? HomeEndKeys.Rxvt : HomeEndKeys.Standard;
        if (Has("LinuxFunctionKeys"))
        {
            kb.FunctionKeys = Int("LinuxFunctionKeys", 0) switch
            {
                1 => FunctionKeys.Linux, 2 => FunctionKeys.XtermR6, 3 => FunctionKeys.Vt400, 4 => FunctionKeys.Vt100Plus,
                5 => FunctionKeys.Sco, 6 => FunctionKeys.Xterm216, _ => FunctionKeys.Tilde,
            };
            done.Add("Keyboard: backspace, Home/End and function key modes");
        }
        if (Has("ShiftedArrowKeys")) kb.ShiftedArrows = Int("ShiftedArrowKeys", 1) == 0 ? ShiftedArrowKeys.CtrlTogglesApplicationMode : ShiftedArrowKeys.XtermModifiers;
        kb.ApplicationCursorKeys = Bool("ApplicationCursorKeys", kb.ApplicationCursorKeys);
        if (Bool("NetHackKeypad", false)) kb.Keypad = InitialKeypad.NetHack;
        else if (Bool("ApplicationKeypad", false)) kb.Keypad = InitialKeypad.Application;
        kb.CtrlAltIsAltGr = Bool("CtrlAltKeys", kb.CtrlAltIsAltGr);
        kb.AltF4Closes = Bool("AltF4", kb.AltF4Closes);
        kb.AltSpaceSystemMenu = Bool("AltSpace", kb.AltSpaceSystemMenu);

        // Bell
        if (Has("Beep"))
        {
            s.Bell.Style = Int("Beep", 1) switch { 0 => BellStyle.None, 2 => BellStyle.Visual, 3 => BellStyle.WaveFile, 4 => BellStyle.Beep, _ => BellStyle.SystemDefault };
            s.Bell.WaveFile = Str("BellWaveFile", s.Bell.WaveFile);
            s.Bell.Taskbar = Int("BeepInd", 0) switch { 1 => TaskbarBell.Flashing, 2 => TaskbarBell.Steady, _ => TaskbarBell.Disabled };
            s.Bell.OverloadProtection = Bool("BellOverload", true);
            s.Bell.OverloadCount = Int("BellOverloadN", 5);
        }

        // Selection
        if (Has("MouseIsXterm"))
        {
            switch (Int("MouseIsXterm", 0))
            {
                case 1: s.Selection.RightClick = RightClickAction.ExtendSelection; s.Selection.MiddleClick = MiddleClickAction.Paste; break;
                case 2: s.Selection.RightClick = RightClickAction.ContextMenu; s.Selection.MiddleClick = MiddleClickAction.ExtendSelection; break;
                default: s.Selection.RightClick = RightClickAction.Paste; s.Selection.MiddleClick = MiddleClickAction.ExtendSelection; break;
            }
            done.Add("Mouse button behaviour");
        }
        s.Selection.CopyOnSelect = Bool("MouseAutocopy", s.Selection.CopyOnSelect);
        s.Selection.PasteControlCharacters = Bool("PasteControls", s.Selection.PasteControlCharacters);
        s.Selection.ShiftOverridesMouseReporting = Bool("MouseOverride", s.Selection.ShiftOverridesMouseReporting);

        // Translation
        var cp = Str("LineCodePage");
        if (cp.Length > 0) s.Translation.RemoteCharset = cp.StartsWith("UTF-8", StringComparison.OrdinalIgnoreCase) ? "UTF-8" : MapCodePage(cp) ?? s.Translation.RemoteCharset;
        s.Translation.CjkAmbiguousWide = Bool("CJKAmbigWide", s.Translation.CjkAmbiguousWide);

        // Window behaviour
        if (Has("CloseOnExit"))
            s.Interface.CloseTabOnExit = Int("CloseOnExit", 1) switch { 0 => CloseOnExit.Never, 2 => CloseOnExit.Always, _ => CloseOnExit.OnCleanExit };
        s.Interface.WarnOnCloseActive = Bool("WarnOnClose", s.Interface.WarnOnCloseActive);
        s.Interface.FullScreenOnAltEnter = Bool("FullScreenOnAltEnter", s.Interface.FullScreenOnAltEnter);

        // Connection
        var c = s.Connection;
        int ping = Int("PingIntervalSecs", 0) + Int("PingInterval", 0) * 60;
        if (ping > 0) c.KeepaliveSeconds = ping;
        c.TcpNoDelay = Bool("TCPNoDelay", c.TcpNoDelay);
        c.TcpKeepalives = Bool("TCPKeepalives", c.TcpKeepalives);
        c.AddressFamily = Int("AddressFamily", 0) switch { 1 => AddressFamilyChoice.IPv4, 2 => AddressFamilyChoice.IPv6, _ => c.AddressFamily };
        var user = Str("UserName");
        if (user.Length > 0) c.AutoLoginUsername = user;
        var term = Str("TerminalType");
        if (term.Length > 0 && term != "xterm") c.TerminalType = term;
        if (Has("TerminalSpeed")) c.TerminalSpeed = Str("TerminalSpeed", c.TerminalSpeed);

        // Proxy (PuTTY's password is stored in clear; re-protect it)
        int proxy = Int("ProxyMethod", 0);
        if (proxy is 1 or 2 or 3)
        {
            s.Proxy.Type = proxy switch { 1 => ProxyKind.Socks4, 2 => ProxyKind.Socks5, _ => ProxyKind.Http };
            s.Proxy.Host = Str("ProxyHost", s.Proxy.Host);
            s.Proxy.Port = Int("ProxyPort", s.Proxy.Port);
            s.Proxy.Username = Str("ProxyUsername");
            var pw = Str("ProxyPassword");
            if (pw.Length > 0) s.Proxy.Password = SecretStore.Protect(pw) ?? "";
            s.Proxy.ExcludeList = Str("ProxyExcludeList");
            done.Add($"Proxy: {s.Proxy.Type} {s.Proxy.Host}:{s.Proxy.Port}");
        }

        // SSH
        s.Ssh.Compression = Bool("Compression", s.Ssh.Compression);
        s.Ssh.TryKeyboardInteractive = Bool("AuthKI", s.Ssh.TryKeyboardInteractive);
        s.Ssh.ShowBanner = Bool("SshBanner", s.Ssh.ShowBanner);
        s.Ssh.NoShell = Bool("SshNoShell", s.Ssh.NoShell);
        s.Ssh.NoPty = Bool("NoPTY", s.Ssh.NoPty);
        var key = Str("PublicKeyFile");
        if (key.Length > 0) { s.Ssh.PrivateKeyFile = key; done.Add("Default private key file"); }

        // Serial
        s.Serial.Line = Str("SerialLine", s.Serial.Line);
        s.Serial.Speed = Int("SerialSpeed", s.Serial.Speed);
        s.Serial.DataBits = Int("SerialDataBits", s.Serial.DataBits);

        // Telnet
        s.Telnet.PassiveNegotiation = Bool("PassiveTelnet", s.Telnet.PassiveNegotiation);
        s.Telnet.KeyboardSendsTelnetCommands = Bool("TelnetKey", s.Telnet.KeyboardSendsTelnetCommands);
        s.Telnet.ReturnSendsTelnetNewline = Bool("TelnetRet", s.Telnet.ReturnSendsTelnetNewline);
        s.Telnet.RfcEnvironment = Bool("RFCEnviron", s.Telnet.RfcEnvironment);

        // Logging
        if (Int("LogType", 0) > 0)
        {
            s.Logging.Mode = Int("LogType", 0) == 1 ? LogMode.Printable : LogMode.AllOutput;
            var file = Str("LogFileName");
            if (file.Length > 0 && !file.Equals("putty.log", StringComparison.OrdinalIgnoreCase)) s.Logging.FileName = file;
            s.Logging.WhenExists = Int("LogFileClash", -1) switch { 1 => LogFileExists.Overwrite, 0 => LogFileExists.Append, _ => LogFileExists.Ask };
            done.Add("Session logging");
        }

        if (done.Count == 0) done.Add("Terminal and keyboard options");
        return done;
    }

    /// <summary>Imports PuTTY sessions; returns how many were added (or replaced when overwrite is true).</summary>
    public static int ImportSessions(bool overwrite)
    {
        int count = 0;
        foreach (var p in ReadSessions())
        {
            var existing = SettingsStore.Sessions.Sessions.FirstOrDefault(s => s.Name == p.Name && s.Folder == p.Folder);
            if (existing != null)
            {
                if (!overwrite) continue;
                p.Id = existing.Id;
                SettingsStore.Sessions.Sessions[SettingsStore.Sessions.Sessions.IndexOf(existing)] = p;
            }
            else SettingsStore.Sessions.Sessions.Add(p);
            count++;
        }
        if (count > 0) SettingsStore.SaveSessions();
        return count;
    }
}
