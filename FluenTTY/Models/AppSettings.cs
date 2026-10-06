namespace FluenTTY.Models;

/// <summary>
/// Everything under Settings. Each property of this class is one export/import category
/// (Settings › Backup) and one JSON object in settings.json; keep that 1:1 when adding sections.
/// Protocol sections hold defaults; a saved session can override the connection-specific ones.
/// Option names follow PuTTY's conf.h where there is an equivalent (noted in comments).
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public InterfaceSettings Interface { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public BellSettings Bell { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public SelectionSettings Selection { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
    public SshSettings Ssh { get; set; } = new();
    public TelnetSettings Telnet { get; set; } = new();
    public RloginSettings Rlogin { get; set; } = new();
    public SerialSettings Serial { get; set; } = new();
    public RdpSettings Rdp { get; set; } = new();
    public VncSettings Vnc { get; set; } = new();
    public XdmcpSettings Xdmcp { get; set; } = new();
    public FileTransferSettings Files { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
}

public sealed class InterfaceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    public bool Animations { get; set; } = true;
    public bool ShowSessionSidebar { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    public TabCloseButtons TabCloseButtons { get; set; } = TabCloseButtons.Always;
    public NewTabPosition NewTabPosition { get; set; } = NewTabPosition.End;
    public double MaxTabWidth { get; set; } = 220;
    public TabTitleSource TabTitle { get; set; } = TabTitleSource.RemoteTitle;   // win_name_always
    public bool WarnOnCloseActive { get; set; } = true;                           // warn_on_close
    public bool WarnOnExitWithSessions { get; set; } = true;
    public CloseOnExit CloseTabOnExit { get; set; } = CloseOnExit.OnCleanExit;    // close_on_exit
    public StartupAction Startup { get; set; } = StartupAction.StartPage;
    public bool SingleInstance { get; set; } = true;
    public bool AlwaysOnTop { get; set; }                                          // alwaysontop
    public bool FullScreenOnAltEnter { get; set; }                                 // fullscreenonaltenter
    public int PopOutColumns { get; set; } = 100;
    public int PopOutRows { get; set; } = 32;
    public bool ReconnectKey { get; set; } = true;

    // Crab screensaver for terminal tabs — MobaXterm's penguin "consolesaver" (MobaXterm.ini [Misc] names in comments).
    public bool Screensaver { get; set; } = true;
    public int ScreensaverMinutes { get; set; } = 10;                              // TimerConst (seconds there)
    public int ScreensaverCrabs { get; set; } = 4;                                 // NbPenguins
    public CrabSpeed ScreensaverSpeed { get; set; } = CrabSpeed.Normal;            // TimerInt
    public CrabSize ScreensaverCrabSize { get; set; } = CrabSize.Medium;
    public bool ScreensaverKeepalive { get; set; } = true;

    // Settings › Updates (Services\Updater). What the updater remembers between runs (last version run,
    // skipped version) lives in update-state.json instead, so it never travels in an export.
    public bool CheckForUpdates { get; set; } = true;
    public bool ShowWhatsNew { get; set; } = true;

    /// <summary>The first-run welcome/import guide has been shown.</summary>
    public bool OnboardingDone { get; set; }
}

/// <summary>PuTTY: Terminal and Terminal › Features.</summary>
public sealed class TerminalSettings
{
    public bool AutoWrap { get; set; } = true;                 // wrap_mode
    public bool DecOriginMode { get; set; }                    // dec_om
    public bool LfImpliesCr { get; set; }                      // lfhascr
    public bool CrImpliesLf { get; set; }                      // crhaslf
    public bool BackgroundColourErase { get; set; } = true;    // bce
    public bool BlinkText { get; set; }                        // blinktext
    public string Answerback { get; set; } = "FluenTTY";       // answerback
    public AutoOnOff LocalEcho { get; set; } = AutoOnOff.Auto; // localecho
    public AutoOnOff LocalLineEditing { get; set; } = AutoOnOff.Auto; // localedit
    public int ScrollbackLines { get; set; } = 10000;          // savelines
    public bool EraseToScrollback { get; set; } = true;        // erase_to_scrollback
    public bool ScrollOnKeypress { get; set; } = true;         // scroll_on_key
    public bool ScrollOnOutput { get; set; }                   // scroll_on_disp

    // Features
    public bool NoApplicationCursorKeys { get; set; }          // no_applic_c
    public bool NoApplicationKeypad { get; set; }              // no_applic_k
    public bool NoMouseReporting { get; set; }                 // no_mouse_rep
    public bool NoRemoteResize { get; set; }                   // no_remote_resize
    public bool NoAltScreen { get; set; }                      // no_alt_screen
    public bool NoRemoteWindowTitle { get; set; }              // no_remote_wintitle
    public bool NoRemoteClearScrollback { get; set; }          // no_remote_clearscroll
    public bool NoDestructiveBackspace { get; set; }           // no_dbackspace
    public bool NoRemoteCharset { get; set; }                  // no_remote_charset
    public bool NoBracketedPaste { get; set; }                 // no_bracketed_paste
    public Terminal.TitleQueryAction TitleQuery { get; set; } = Terminal.TitleQueryAction.Empty; // remote_qtitle_action
    public bool AllowRemoteClipboardWrite { get; set; }
    public bool WheelSendsArrowsInAltScreen { get; set; } = true;
}

/// <summary>PuTTY: Terminal › Keyboard (plus window-level shortcuts).</summary>
public sealed class KeyboardSettings
{
    public BackspaceKey Backspace { get; set; } = BackspaceKey.ControlQuestion;   // bksp_is_delete
    public HomeEndKeys HomeEnd { get; set; } = HomeEndKeys.Xterm;                 // rxvt_homeend
    public FunctionKeys FunctionKeys { get; set; } = FunctionKeys.Xterm216;       // funky_type
    public ShiftedArrowKeys ShiftedArrows { get; set; } = ShiftedArrowKeys.XtermModifiers; // sharrow_type
    public bool ApplicationCursorKeys { get; set; }                               // app_cursor
    public InitialKeypad Keypad { get; set; } = InitialKeypad.Normal;            // app_keypad / nethack_keypad
    public bool CtrlAltIsAltGr { get; set; } = true;                              // ctrlaltkeys
    public bool AltSendsEscape { get; set; } = true;
    public bool AltF4Closes { get; set; } = true;                                  // alt_f4
    public bool AltSpaceSystemMenu { get; set; }                                   // alt_space
    public bool CtrlShiftCopyPaste { get; set; } = true;                           // ctrlshiftcv
    public bool CtrlInsertShiftInsert { get; set; } = true;                        // ctrlshiftins
    public bool TabShortcuts { get; set; } = true;
    public bool CtrlWheelZoom { get; set; } = true;
}

/// <summary>PuTTY: Terminal › Bell.</summary>
public sealed class BellSettings
{
    public BellStyle Style { get; set; } = BellStyle.SystemDefault;  // beep
    public string WaveFile { get; set; } = "";                       // bell_wavefile
    public TaskbarBell Taskbar { get; set; } = TaskbarBell.Flashing; // beep_ind
    public bool TabIndicator { get; set; } = true;
    public bool OverloadProtection { get; set; } = true;             // bellovl
    public int OverloadCount { get; set; } = 5;                      // bellovl_n
    public double OverloadSeconds { get; set; } = 2;                 // bellovl_t
    public double OverloadSilenceSeconds { get; set; } = 5;          // bellovl_s
}

/// <summary>PuTTY: Window › Appearance and Window › Colours.</summary>
public sealed class AppearanceSettings
{
    public string FontFamily { get; set; } = "Cascadia Mono";   // font
    public double FontSize { get; set; } = 11;                   // points
    public FontWeightKind FontWeight { get; set; } = FontWeightKind.Normal;
    public FontQuality FontQuality { get; set; } = FontQuality.Default; // font_quality
    public BoldStyle BoldStyle { get; set; } = BoldStyle.Both;   // bold_style
    public double LineHeight { get; set; } = 1.0;
    public double Padding { get; set; } = 8;
    public CursorShape CursorShape { get; set; } = CursorShape.Block; // cursor_type
    public bool CursorBlink { get; set; } = true;                // blink_cur
    public string SchemeDark { get; set; } = ColorScheme.DefaultDark;
    public string SchemeLight { get; set; } = ColorScheme.DefaultLight;
    public List<ColorScheme> CustomSchemes { get; set; } = new();
    public int BackgroundOpacity { get; set; } = 100;
    public bool AnsiColour { get; set; } = true;                 // ansi_colour
    public bool Xterm256Colour { get; set; } = true;             // xterm_256_colour
    public bool TrueColour { get; set; } = true;                 // true_colour
    public bool ShowScrollbar { get; set; } = true;              // scrollbar
    public bool ScrollbarInFullScreen { get; set; }              // scrollbar_in_fullscreen
    public bool HideMouseWhileTyping { get; set; }               // hide_mouseptr
}

/// <summary>PuTTY: Window › Selection (and paste handling).</summary>
public sealed class SelectionSettings
{
    public RightClickAction RightClick { get; set; } = RightClickAction.Paste;      // mouse_is_xterm
    public MiddleClickAction MiddleClick { get; set; } = MiddleClickAction.ExtendSelection;
    public bool CopyOnSelect { get; set; } = true;                                  // mouseautocopy
    public bool AltDragRectangular { get; set; } = true;                            // rect_select
    public bool ShiftOverridesMouseReporting { get; set; } = true;                  // mouse_override
    public string WordDelimiters { get; set; } = " \t()[]{}<>'\"`,;:|│";            // wordness
    public bool TrimTrailingWhitespace { get; set; } = true;
    public bool PasteControlCharacters { get; set; }                                // paste_controls
    public bool WarnMultiLinePaste { get; set; } = true;
    public bool WarnLargePaste { get; set; } = true;
    public bool CopyWithFormatting { get; set; }                                    // rtf_paste
}

/// <summary>PuTTY: Window › Translation.</summary>
public sealed class TranslationSettings
{
    public string RemoteCharset { get; set; } = "UTF-8";          // line_codepage
    public bool CjkAmbiguousWide { get; set; }                    // cjk_ambig_wide
    public bool Utf8Override { get; set; } = true;                // utf8_override
    public LineDrawing LineDrawing { get; set; } = LineDrawing.Unicode; // vtmode
}

/// <summary>PuTTY: Session › Logging.</summary>
public sealed class LoggingSettings
{
    public LogMode Mode { get; set; } = LogMode.Off;                     // logtype
    public string FileName { get; set; } = @"%USERPROFILE%\Documents\FluenTTY Logs\&H-&Y&M&D-&T.log"; // logfilename
    public LogFileExists WhenExists { get; set; } = LogFileExists.Append; // logxfovr
    public bool Flush { get; set; } = true;                              // logflush
    public bool Header { get; set; } = true;                             // logheader
    public bool Timestamps { get; set; }
}

/// <summary>PuTTY: Connection and Connection › Data.</summary>
public sealed class ConnectionSettings
{
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int KeepaliveSeconds { get; set; } = 0;              // ping_interval
    public bool TcpNoDelay { get; set; } = true;                // tcp_nodelay
    public bool TcpKeepalives { get; set; }                     // tcp_keepalives
    public AddressFamilyChoice AddressFamily { get; set; } = AddressFamilyChoice.Auto; // addressfamily
    public string AutoLoginUsername { get; set; } = "";         // username
    public bool UseWindowsUsername { get; set; }                // username_from_env
    public string TerminalType { get; set; } = "xterm-256color"; // termtype
    public string TerminalSpeed { get; set; } = "38400,38400";  // termspeed
    public List<EnvVar> Environment { get; set; } = new();      // environmt
}

public sealed class EnvVar
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>PuTTY: Connection › Proxy.</summary>
public sealed class ProxySettings
{
    public ProxyKind Type { get; set; } = ProxyKind.None;       // proxy_type
    public string Host { get; set; } = "proxy";                 // proxy_host
    public int Port { get; set; } = 80;                         // proxy_port
    public string Username { get; set; } = "";                  // proxy_username
    public string Password { get; set; } = "";                  // proxy_password (protected)
    public string ExcludeList { get; set; } = "";               // proxy_exclude_list
    public bool ProxyLocalhost { get; set; }                    // even_proxy_localhost
    public AutoOnOff DnsAtProxy { get; set; } = AutoOnOff.Auto; // proxy_dns
}

public sealed class AlgorithmPreference
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class PortForward
{
    public PortForwardKind Kind { get; set; } = PortForwardKind.Local;
    public string BindAddress { get; set; } = "";
    public int SourcePort { get; set; }
    public string DestinationHost { get; set; } = "";
    public int DestinationPort { get; set; }

    public override string ToString() => Kind switch
    {
        PortForwardKind.Dynamic => $"D {(BindAddress.Length > 0 ? BindAddress + ":" : "")}{SourcePort} (SOCKS)",
        PortForwardKind.Local => $"L {(BindAddress.Length > 0 ? BindAddress + ":" : "")}{SourcePort} → {DestinationHost}:{DestinationPort}",
        _ => $"R {(BindAddress.Length > 0 ? BindAddress + ":" : "")}{SourcePort} → {DestinationHost}:{DestinationPort}",
    };
}

/// <summary>PuTTY: Connection › SSH and its sub-panels.</summary>
public sealed class SshSettings
{
    public bool Compression { get; set; }                       // compression
    public string InitialCommand { get; set; } = "";            // remote_cmd (typed after login)
    public bool NoShell { get; set; }                           // ssh_no_shell
    public bool NoPty { get; set; }                             // nopty
    public bool PreferKnownHostKeys { get; set; } = true;       // ssh_prefer_known_hostkeys
    public List<AlgorithmPreference> KexAlgorithms { get; set; } = new();     // ssh_kexlist
    public List<AlgorithmPreference> HostKeyAlgorithms { get; set; } = new(); // ssh_hklist
    public List<AlgorithmPreference> Ciphers { get; set; } = new();           // ssh_cipherlist
    public List<AlgorithmPreference> Macs { get; set; } = new();
    public List<string> ManualHostKeys { get; set; } = new();   // ssh_manual_hostkeys (SHA256 fingerprints)
    public bool TryKeyboardInteractive { get; set; } = true;    // try_ki_auth
    public bool TryPassword { get; set; } = true;
    public bool ShowBanner { get; set; } = true;                // ssh_show_banner
    public string PrivateKeyFile { get; set; } = "";            // keyfile
    public int MaxAuthAttempts { get; set; } = 3;
    public List<PortForward> PortForwards { get; set; } = new(); // portfwd
    public bool LocalPortsAcceptAll { get; set; }              // lport_acceptall
    public bool RemotePortsAcceptAll { get; set; }             // rport_acceptall
    public bool OpenSftpBrowser { get; set; } = true;
    public bool SftpFollowTerminalFolder { get; set; } = true;
}

/// <summary>PuTTY: Connection › Telnet.</summary>
public sealed class TelnetSettings
{
    public bool RfcEnvironment { get; set; }                    // rfc_environ
    public bool PassiveNegotiation { get; set; }                // passive_telnet
    public bool KeyboardSendsTelnetCommands { get; set; }       // telnet_keyboard
    public bool ReturnSendsTelnetNewline { get; set; } = true;  // telnet_newline
}

/// <summary>PuTTY: Connection › Rlogin (also used for rsh).</summary>
public sealed class RloginSettings
{
    public string LocalUsername { get; set; } = "";             // localusername
    public string RshCommand { get; set; } = "";
}

/// <summary>PuTTY: Connection › Serial.</summary>
public sealed class SerialSettings
{
    public string Line { get; set; } = "COM1";                  // serline
    public int Speed { get; set; } = 9600;                      // serspeed
    public int DataBits { get; set; } = 8;                      // serdatabits
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One; // serstopbits
    public SerialParity Parity { get; set; } = SerialParity.None;     // serparity
    public SerialFlow FlowControl { get; set; } = SerialFlow.XonXoff; // serflow
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;
    public int BreakMilliseconds { get; set; } = 250;
}

public sealed class RdpSettings
{
    public RdpSizeMode SizeMode { get; set; } = RdpSizeMode.FitToWindow;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public RdpResizeBehavior ResizeBehavior { get; set; } = RdpResizeBehavior.DynamicResolution;
    public int ColorDepth { get; set; } = 32;
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectSmartCards { get; set; }
    public bool RedirectPorts { get; set; }
    public RdpAudio Audio { get; set; } = RdpAudio.PlayOnThisComputer;
    public bool AudioCapture { get; set; }
    public RdpKeyboardHook KeyboardHook { get; set; } = RdpKeyboardHook.OnlyInFullScreen;
    public bool NetworkLevelAuthentication { get; set; } = true;
    public RdpServerAuth ServerAuthentication { get; set; } = RdpServerAuth.WarnMe;
    public bool AdminSession { get; set; }
    public RdpConnectionSpeed ConnectionSpeed { get; set; } = RdpConnectionSpeed.AutoDetect;
    public bool DesktopBackground { get; set; } = true;
    public bool FontSmoothing { get; set; } = true;
    public bool DesktopComposition { get; set; } = true;
    public bool ShowWindowContentsWhileDragging { get; set; } = true;
    public bool MenuAnimations { get; set; } = true;
    public bool VisualStyles { get; set; } = true;
    public bool PersistentBitmapCaching { get; set; } = true;
    public bool Compression { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool ConnectionBarInFullScreen { get; set; } = true;
    public RdpGatewayMode GatewayMode { get; set; } = RdpGatewayMode.None;
    public string GatewayHost { get; set; } = "";
    public bool GatewayUseSameCredentials { get; set; } = true;
}

public sealed class VncSettings
{
    public VncEncoding Encoding { get; set; } = VncEncoding.Auto;
    public VncColorDepth ColorDepth { get; set; } = VncColorDepth.TrueColor;
    public VncScaling Scaling { get; set; } = VncScaling.FitToWindow;
    public bool Shared { get; set; } = true;
    public bool ViewOnly { get; set; }
    public bool ClipboardSharing { get; set; } = true;
    public bool ShowRemoteCursor { get; set; } = true;
    public bool SendSpecialKeys { get; set; } = true;
}

public sealed class XdmcpSettings
{
    public string XServerPath { get; set; } = "";
    public string ExtraArguments { get; set; } = "";
    public XdmcpQuery QueryMode { get; set; } = XdmcpQuery.Query;
    public XWindowMode WindowMode { get; set; } = XWindowMode.SingleWindow;
    public string Resolution { get; set; } = "1280x800";
    public int DisplayNumber { get; set; } = 0; // 0 = first free
    public bool ClipboardSync { get; set; } = true;
    public bool StopServerWithTab { get; set; } = true;
}

public sealed class FileTransferSettings
{
    public FtpEncryption FtpEncryption { get; set; } = FtpEncryption.Auto;
    public FtpDataMode FtpDataMode { get; set; } = FtpDataMode.AutoPassive;
    public CertificatePolicy Certificates { get; set; } = CertificatePolicy.Ask;
    public string FtpEncoding { get; set; } = "UTF-8";
    public bool ShowHiddenFiles { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmOverwrite { get; set; } = true;
    public bool PreserveTimestamps { get; set; } = true;
    public string DownloadFolder { get; set; } = "";
    public FileDoubleClick DoubleClick { get; set; } = FileDoubleClick.Edit;
    public int MaxEditSizeMB { get; set; } = 10;
}

public sealed class EditorSettings
{
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 11;
    public bool WordWrap { get; set; }
    public bool ShowLineNumbers { get; set; } = true;
    public bool HighlightCurrentLine { get; set; } = true;
    public bool SyntaxHighlighting { get; set; } = true;
    public int TabSize { get; set; } = 4;
    public bool ConvertTabsToSpaces { get; set; }
    public bool ShowWhitespace { get; set; }
    public EditorPlacement OpenIn { get; set; } = EditorPlacement.Tab;
    public string ExternalEditor { get; set; } = "";
}
