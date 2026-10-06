using System.Text;

namespace FluenTTY.Terminal;

public enum TitleQueryAction { None, Empty, Title }

public enum MouseTracking { None, X10, Normal, ButtonEvent, AnyEvent }

public enum MouseEncoding { Default, Utf8, Sgr, Urxvt }

/// <summary>DECSCUSR cursor shape requested by the remote side (overrides the configured one until reset).</summary>
public enum RemoteCursorShape { Default, Block, Underline, Bar }

/// <summary>
/// Emulation switches, mostly the ones PuTTY exposes under Terminal and Terminal › Features
/// (conf.h: wrap_mode, dec_om, lfhascr, crhaslf, bce, answerback, no_alt_screen, ...).
/// </summary>
public sealed class TerminalOptions
{
    public bool AutoWrap { get; set; } = true;
    public bool DecOriginMode { get; set; }
    public bool LfImpliesCr { get; set; }
    public bool CrImpliesLf { get; set; }
    public bool BackgroundColourErase { get; set; } = true;
    public string Answerback { get; set; } = "FluenTTY";
    public bool EraseToScrollback { get; set; } = true;
    public int ScrollbackLines { get; set; } = 10000;
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool CjkAmbiguousWide { get; set; }
    public bool Utf8Override { get; set; } = true;
    public bool AnsiColour { get; set; } = true;
    public bool Xterm256Colour { get; set; } = true;
    public bool TrueColour { get; set; } = true;

    // Terminal › Features
    public bool NoApplicationCursorKeys { get; set; }
    public bool NoApplicationKeypad { get; set; }
    public bool NoMouseReporting { get; set; }
    public bool NoRemoteResize { get; set; }
    public bool NoAltScreen { get; set; }
    public bool NoRemoteWindowTitle { get; set; }
    public bool NoRemoteClearScrollback { get; set; }
    public bool NoDestructiveBackspace { get; set; }
    public bool NoRemoteCharset { get; set; }
    public bool NoBracketedPaste { get; set; }
    public TitleQueryAction TitleQuery { get; set; } = TitleQueryAction.Empty;
    /// <summary>OSC 52: lets the remote side put text on the Windows clipboard.</summary>
    public bool AllowRemoteClipboardWrite { get; set; }
}
