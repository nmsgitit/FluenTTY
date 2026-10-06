using System.Text.Json.Serialization;

namespace FluenTTY.Models;

/// <summary>
/// A saved session (PuTTY's "Saved Sessions"): where to connect plus the handful of settings that are
/// commonly per-host. Null overrides mean "use the default from Settings".
/// </summary>
public sealed class SessionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Folder path in the sessions sidebar, "/" separated ("" = top level).</summary>
    public string Folder { get; set; } = "";
    public ProtocolKind Protocol { get; set; } = ProtocolKind.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Saved password, protected with Windows DPAPI for the current user (see SecretStore).</summary>
    public string? Password { get; set; }
    public string PrivateKeyFile { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? LastUsed { get; set; }

    // Terminal overrides
    public string? TerminalType { get; set; }
    public string? ColorScheme { get; set; }
    public double? FontSize { get; set; }
    public string? RemoteCharset { get; set; }
    public string? InitialCommand { get; set; }
    public int? KeepaliveSeconds { get; set; }
    public bool UseProxy { get; set; } = true;
    public LogMode? LogMode { get; set; }
    /// <summary>
    /// Login script: wait for text matching Expect (a regular expression) and type Send (with
    /// {username} / {password} substituted) — PuTTY Connection Manager's "login macro", Tabby's
    /// "login scripts". Runs before the initial command.
    /// </summary>
    public List<LoginStep> LoginScript { get; set; } = new();

    // SSH
    public List<PortForward> PortForwards { get; set; } = new();
    public bool? OpenSftpBrowser { get; set; }
    public bool? Compression { get; set; }
    public bool? X11Forwarding { get; set; }

    // Serial
    public int? SerialSpeed { get; set; }
    public int? SerialDataBits { get; set; }
    public SerialStopBits? SerialStopBits { get; set; }
    public SerialParity? SerialParity { get; set; }
    public SerialFlow? SerialFlow { get; set; }

    // Rsh
    public string? RshCommand { get; set; }

    // RDP
    public string Domain { get; set; } = "";
    public bool? RdpAdminSession { get; set; }
    public RdpSizeMode? RdpSizeMode { get; set; }
    public int? RdpWidth { get; set; }
    public int? RdpHeight { get; set; }

    // VNC
    public bool? VncViewOnly { get; set; }

    // FTP
    public FtpEncryption? FtpEncryption { get; set; }

    // XDMCP
    public XdmcpQuery? XdmcpQuery { get; set; }

    [JsonIgnore]
    public string DisplayTarget => Protocol switch
    {
        ProtocolKind.Serial => Host,
        _ => string.IsNullOrEmpty(Username) ? HostWithPort : $"{Username}@{HostWithPort}",
    };

    [JsonIgnore]
    public string HostWithPort => Port == 0 || Port == DefaultPort(Protocol) ? Host : $"{Host}:{Port}";

    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : DefaultPort(Protocol);

    public static int DefaultPort(ProtocolKind p) => p switch
    {
        ProtocolKind.Ssh or ProtocolKind.Sftp => 22,
        ProtocolKind.Telnet => 23,
        ProtocolKind.Rlogin => 513,
        ProtocolKind.Rsh => 514,
        ProtocolKind.Ftp => 21,
        ProtocolKind.Rdp => 3389,
        ProtocolKind.Vnc => 5900,
        ProtocolKind.Xdmcp => 177,
        _ => 0,
    };

    public static string ProtocolName(ProtocolKind p) => p switch
    {
        ProtocolKind.Ssh => "SSH",
        ProtocolKind.Telnet => "Telnet",
        ProtocolKind.Rlogin => "Rlogin",
        ProtocolKind.Rsh => "Rsh",
        ProtocolKind.Raw => "Raw TCP",
        ProtocolKind.Serial => "Serial",
        ProtocolKind.Sftp => "SFTP",
        ProtocolKind.Ftp => "FTP",
        ProtocolKind.Rdp => "RDP",
        ProtocolKind.Vnc => "VNC",
        ProtocolKind.Xdmcp => "XDMCP",
        _ => p.ToString(),
    };

    /// <summary>Segoe Fluent Icons glyph for the protocol.</summary>
    public static string ProtocolGlyph(ProtocolKind p) => p switch
    {
        ProtocolKind.Ssh => "",     // CommandPrompt
        ProtocolKind.Telnet => "",  // Network
        ProtocolKind.Rlogin or ProtocolKind.Rsh => "",
        ProtocolKind.Raw => "",     // Code
        ProtocolKind.Serial => "",  // USB
        ProtocolKind.Sftp or ProtocolKind.Ftp => "", // Folder
        ProtocolKind.Rdp => "",     // TVMonitor
        ProtocolKind.Vnc => "",     // DeviceLaptop
        ProtocolKind.Xdmcp => "",
        _ => "",
    };

    public static bool IsTerminal(ProtocolKind p) => p is ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Rlogin
        or ProtocolKind.Rsh or ProtocolKind.Raw or ProtocolKind.Serial;

    public SessionProfile Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Services.Json.Options);
        return System.Text.Json.JsonSerializer.Deserialize<SessionProfile>(json, Services.Json.Options)!;
    }
}

public sealed class LoginStep
{
    public string Expect { get; set; } = "";
    public string Send { get; set; } = "";
    /// <summary>Don't echo/log what's sent (passwords).</summary>
    public bool Secret { get; set; }

    public static List<LoginStep> UsernamePassword() => new()
    {
        new LoginStep { Expect = @"(?i)(login|user ?name)\s*:\s*$", Send = "{username}" },
        new LoginStep { Expect = @"(?i)password\s*:\s*$", Send = "{password}", Secret = true },
    };
}

public sealed class SessionStoreData
{
    public int SchemaVersion { get; set; } = 1;
    public List<SessionProfile> Sessions { get; set; } = new();
    /// <summary>Empty folders the user created (folders with sessions exist implicitly).</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>Sessions open when the app last closed (for Startup = Restore).</summary>
    public List<SessionProfile> LastOpen { get; set; } = new();
    public List<SessionProfile> Recent { get; set; } = new();
}

/// <summary>SSH host key the user has accepted (PuTTY's SshHostKeys).</summary>
public sealed class KnownHostKey
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Algorithm { get; set; } = "";
    public string FingerprintSha256 { get; set; } = "";
    public string KeyBase64 { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string Key => $"{Algorithm}@{Port}:{Host}";
}
