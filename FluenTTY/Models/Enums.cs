namespace FluenTTY.Models;

public enum ProtocolKind { Ssh, Telnet, Rlogin, Rsh, Raw, Serial, Sftp, Ftp, Rdp, Vnc, Xdmcp }

public enum AppTheme { System, Light, Dark }
public enum BackdropKind { Mica, MicaAlt, Acrylic, None }
public enum TabCloseButtons { Always, Hover, Never }
public enum NewTabPosition { End, AfterCurrent }
public enum CloseOnExit { Always, Never, OnCleanExit }
public enum StartupAction { StartPage, RestoreLastSessions, Nothing }
public enum TabTitleSource { RemoteTitle, SessionName, HostName }
public enum CrabSpeed { Slow, Normal, Fast }
public enum CrabSize { Small, Medium, Large }

public enum AutoOnOff { Auto, On, Off }

public enum BackspaceKey { ControlQuestion, ControlH }
public enum HomeEndKeys { Xterm, Standard, Rxvt }
public enum FunctionKeys { Xterm216, Tilde, Linux, XtermR6, Vt400, Vt100Plus, Sco }
public enum ShiftedArrowKeys { XtermModifiers, CtrlTogglesApplicationMode }
public enum InitialKeypad { Normal, Application, NetHack }

public enum BellStyle { None, SystemDefault, Visual, WaveFile, Beep }
public enum TaskbarBell { Disabled, Flashing, Steady }

public enum BoldStyle { Font, Colour, Both }
public enum FontQuality { Default, ClearType, Antialiased, NonAntialiased }
public enum CursorShape { Block, Underline, Bar }
public enum FontWeightKind { Light, SemiLight, Normal, Medium, SemiBold, Bold }
public enum LineDrawing { Unicode, Ascii }

public enum RightClickAction { Paste, ContextMenu, ExtendSelection }
public enum MiddleClickAction { Paste, ExtendSelection, None }

public enum LogMode { Off, Printable, AllOutput }
public enum LogFileExists { Ask, Overwrite, Append }

public enum AddressFamilyChoice { Auto, IPv4, IPv6 }
public enum ProxyKind { None, Socks4, Socks5, Http }

public enum SerialStopBits { One, OnePointFive, Two }
public enum SerialParity { None, Odd, Even, Mark, Space }
public enum SerialFlow { None, XonXoff, RtsCts, DsrDtr }

public enum RdpSizeMode { FitToWindow, Fixed }
public enum RdpResizeBehavior { DynamicResolution, SmartSizing, Scrollbars }
public enum RdpAudio { PlayOnThisComputer, PlayOnRemoteComputer, DoNotPlay }
public enum RdpKeyboardHook { OnThisComputer, OnRemoteComputer, OnlyInFullScreen }
public enum RdpServerAuth { WarnMe, ConnectAnyway, DoNotConnect }
public enum RdpConnectionSpeed { AutoDetect, Modem, LowSpeedBroadband, Satellite, HighSpeedBroadband, Wan, Lan }
public enum RdpGatewayMode { None, Automatic, Always }

public enum VncColorDepth { TrueColor, HighColor, Colors256 }
public enum VncEncoding { Auto, Zrle, Hextile, Raw }
public enum VncScaling { FitToWindow, ActualSize }

public enum XdmcpQuery { Query, Broadcast, Indirect }
public enum XWindowMode { SingleWindow, FullScreen }

public enum FtpEncryption { Auto, None, Explicit, Implicit }
public enum FtpDataMode { AutoPassive, Passive, ExtendedPassive, Active }
public enum CertificatePolicy { Ask, AcceptAll, Strict }
public enum FileDoubleClick { Edit, Download, OpenWithDefaultApp }
public enum EditorPlacement { Tab, Window }

public enum PortForwardKind { Local, Remote, Dynamic }
