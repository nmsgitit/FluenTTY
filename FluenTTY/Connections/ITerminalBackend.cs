namespace FluenTTY.Connections;

/// <summary>A command from the tab's "Special commands" menu (PuTTY's specials.h: Break, AYT, ...).</summary>
public sealed record SpecialCommand(string Id, string Title);

/// <summary>
/// Lets a backend ask the user things while connecting. Implemented by the terminal session:
/// prompts appear inside the terminal (like PuTTY's "login as:"), confirmations as dialogs.
/// Methods block the calling (worker) thread until the user answers; null = cancelled.
/// </summary>
public interface IConnectionUi
{
    string? Prompt(string prompt, bool echo);
    /// <summary>Shows host key details and returns: true = trust and remember, false = reject, null = trust once.</summary>
    bool? ConfirmHostKey(HostKeyInfo info);
    bool Confirm(string title, string message, string yes, string no);
    void Status(string message);
    /// <summary>Text displayed in the terminal (e.g. SSH banners), not sent anywhere.</summary>
    void Display(string text);
}

public sealed class HostKeyInfo
{
    public string Host = "";
    public int Port;
    public string Algorithm = "";
    public string FingerprintSha256 = "";
    public string FingerprintMd5 = "";
    public int KeyBits;
    public string KeyBase64 = "";
    /// <summary>A different key is on record for this host (possible man-in-the-middle).</summary>
    public bool Mismatch;
    public string? PreviousFingerprint;
    /// <summary>Set when the key on record is of another type (e.g. ssh-ed25519) than the one offered.</summary>
    public string? PreviousAlgorithm;
}

/// <summary>A connection that carries a terminal byte stream (SSH shell, Telnet, Rlogin, Rsh, Raw, Serial).</summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Raised on a background thread with data from the remote side.</summary>
    event Action<byte[], int>? DataReceived;
    /// <summary>Raised once when the connection ends; the argument is null for a clean exit, else the reason.</summary>
    event Action<string?>? Closed;
    /// <summary>Echo/line-edit hints change (Telnet ECHO negotiation) — for Auto local echo/editing.</summary>
    event Action? LineDisciplineChanged;

    Task ConnectAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct);
    void Send(byte[] data);
    void Resize(int cols, int rows, int pixelWidth, int pixelHeight);
    bool IsConnected { get; }

    /// <summary>What "Auto" local echo means for this protocol right now (PuTTY's backend ldisc option).</summary>
    bool AutoLocalEcho { get; }
    bool AutoLocalEdit { get; }

    IReadOnlyList<SpecialCommand> SpecialCommands { get; }
    void SendSpecial(SpecialCommand command);

    /// <summary>
    /// Sends a protocol-level no-op that the remote program never sees (SSH_MSG_IGNORE, Telnet NOP), to keep
    /// an idle connection from being dropped. False when the protocol has none (Rlogin, Rsh, raw, serial):
    /// anything sent there would reach the remote program as input.
    /// </summary>
    bool SendKeepalive();

    /// <summary>One-line description for the event log / status (cipher, port settings...).</summary>
    string Description { get; }
}
