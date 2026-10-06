using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FluenTTY.Connections;
using FluenTTY.Models;
using FluenTTY.Remote;
using FluenTTY.Services;
using FluenTTY.Terminal;
using FluenTTY.Views;

namespace FluenTTY.Settings;

/// <summary>
/// Every page and option in Settings. Terminal/connection options mirror PuTTY's configuration tree
/// (Session › Logging, Terminal › Keyboard/Bell/Features, Window › Appearance/Behaviour/Translation/
/// Selection/Colours, Connection › Data/Proxy/SSH/Serial/Telnet/Rlogin); keywords carry PuTTY's own
/// names so a search for e.g. "rxvt" or "BCE" finds them.
/// </summary>
public static class SettingsCatalog
{
    private static AppSettings S => SettingsStore.Settings;

    private static (T, string)[] O<T>(params (T, string)[] o) => o;

    private static string LastCheckText() => Updater.LastCheckUtc is { } t
        ? $"Last checked {t.ToLocalTime():d MMMM yyyy, HH:mm}."
        : "Not checked yet.";

    public static List<SettingPage> Build()
    {
        var pages = new List<SettingPage>();

        // ============================================================ General
        pages.Add(new SettingPage { Key = "interface", Title = "Interface", Glyph = "", Group = "General", Description = "How FluenTTY looks and behaves as a window." }.Add(
            new HeaderItem("Theme"),
            new ChoiceItem<AppTheme>("App theme", "Light or dark, or follow Windows.", () => S.Interface.Theme, v => S.Interface.Theme = v,
                O((AppTheme.System, "Use my Windows setting"), (AppTheme.Light, "Light"), (AppTheme.Dark, "Dark")), "dark mode light mode"),
            new ChoiceItem<BackdropKind>("Window material", "Mica tints the window with your desktop wallpaper (Windows 11). Follows Windows' Transparency effects switch.", () => S.Interface.Backdrop, v => S.Interface.Backdrop = v,
                O((BackdropKind.Mica, "Mica"), (BackdropKind.MicaAlt, "Mica Alt"), (BackdropKind.Acrylic, "Acrylic"), (BackdropKind.None, "Solid")), "backdrop transparency"),
            new ToggleItem("Animations", "Tab, page and menu transitions. Windows' own \"Animation effects\" setting also turns them off.", () => S.Interface.Animations, v => S.Interface.Animations = v, "motion transitions"),
            new HeaderItem("Tabs"),
            new ChoiceItem<TabCloseButtons>("Close buttons on tabs", "", () => S.Interface.TabCloseButtons, v => S.Interface.TabCloseButtons = v,
                O((TabCloseButtons.Always, "Always show"), (TabCloseButtons.Hover, "On hover and on the selected tab"), (TabCloseButtons.Never, "Never (middle-click closes)"))),
            new ChoiceItem<NewTabPosition>("Where new tabs open", "", () => S.Interface.NewTabPosition, v => S.Interface.NewTabPosition = v,
                O((NewTabPosition.End, "At the end"), (NewTabPosition.AfterCurrent, "After the current tab"))),
            new NumberItem("Maximum tab width", "Tabs share the title bar and shrink as you open more.", () => S.Interface.MaxTabWidth, v => S.Interface.MaxTabWidth = v, 120, 400, "px"),
            new ChoiceItem<TabTitleSource>("Tab titles", "PuTTY's \"Window title\" / \"Separate window and icon titles\".", () => S.Interface.TabTitle, v => S.Interface.TabTitle = v,
                O((TabTitleSource.RemoteTitle, "Title set by the remote host"), (TabTitleSource.SessionName, "Session name"), (TabTitleSource.HostName, "Host name")), "win_name_always wintitle"),
            new ToggleItem("Show the sessions sidebar", "Saved sessions in folders on the left (Ctrl+Shift+B).", () => S.Interface.ShowSessionSidebar, v => S.Interface.ShowSessionSidebar = v),
            new HeaderItem("Window"),
            new ToggleItem("Keep the window on top", "PuTTY's \"Ensure window is always on top\".", () => S.Interface.AlwaysOnTop, v => S.Interface.AlwaysOnTop = v, "alwaysontop"),
            new ToggleItem("Alt+Enter toggles full screen", "F11 always does. Off lets Alt+Enter reach the remote side.", () => S.Interface.FullScreenOnAltEnter, v => S.Interface.FullScreenOnAltEnter = v, "fullscreenonaltenter"),
            new NumberItem("Pop-out window columns", "Size of a terminal popped out into its own window.", () => S.Interface.PopOutColumns, v => S.Interface.PopOutColumns = (int)v, 40, 400, "columns"),
            new NumberItem("Pop-out window rows", "", () => S.Interface.PopOutRows, v => S.Interface.PopOutRows = (int)v, 10, 200, "rows")));

        pages.Add(new SettingPage { Key = "startup", Title = "Startup and closing", Glyph = "", Group = "General" }.Add(
            new ChoiceItem<StartupAction>("When FluenTTY starts", "", () => S.Interface.Startup, v => S.Interface.Startup = v,
                O((StartupAction.StartPage, "Show the start page"), (StartupAction.RestoreLastSessions, "Reopen the sessions from last time"), (StartupAction.Nothing, "Start page without suggestions"))),
            new ToggleItem("Use one window", "Launching FluenTTY again (or with a command line) opens tabs in the running window.", () => S.Interface.SingleInstance, v => S.Interface.SingleInstance = v, "single instance"),
            new ToggleItem("Warn before closing a connected session", "PuTTY's \"Warn before closing window\".", () => S.Interface.WarnOnCloseActive, v => S.Interface.WarnOnCloseActive = v, "warn_on_close"),
            new ToggleItem("Warn before exiting with sessions open", "", () => S.Interface.WarnOnExitWithSessions, v => S.Interface.WarnOnExitWithSessions = v),
            new ChoiceItem<CloseOnExit>("Close the tab when the session ends", "PuTTY's \"Close window on exit\".", () => S.Interface.CloseTabOnExit, v => S.Interface.CloseTabOnExit = v,
                O((CloseOnExit.OnCleanExit, "Only on clean exit"), (CloseOnExit.Always, "Always"), (CloseOnExit.Never, "Never")), "close_on_exit"),
            new ToggleItem("Press R to reconnect an ended session", "Esc closes it.", () => S.Interface.ReconnectKey, v => S.Interface.ReconnectKey = v, "restart session")));

        pages.Add(new SettingPage { Key = "updates", Title = "Updates", Glyph = "", Group = "General", Description = $"You have FluenTTY {Updater.Current.ToString(3)} ({AppInfo.Flavour.ToLowerInvariant()})." }.Add(
            new ToggleItem("Check for updates when FluenTTY starts", "Asks GitHub whether a newer version is out. If there is one, a bar at the top of the window offers to install it — nothing is downloaded until you choose to. Your settings and saved sessions are kept and backed up before every update.",
                () => S.Interface.CheckForUpdates, v => S.Interface.CheckForUpdates = v, "update check github new version automatic"),
            new ActionItem("Check now", LastCheckText(), "Check now", w => UpdateCheck.Interactive(w), keywords: "update"),
            new ToggleItem("Show what's new after an update", "A short list of what changed, the first time a new version starts.", () => S.Interface.ShowWhatsNew, v => S.Interface.ShowWhatsNew = v, "changelog release notes"),
            new ActionItem("What's new", "Everything that changed in each version.", "Show", w => WhatsNew.ShowAll(w), keywords: "changelog release notes history"),
            new ActionItem("Download page", "Every version of FluenTTY on GitHub, installer and portable.", "Open", _ => Updater.OpenReleasePage(), keywords: "github releases download")));

        // MobaXterm's penguin "consolesaver" (MobaXterm.ini [Misc] TimerConst / NbPenguins / TimerInt), with crabs.
        pages.Add(new SettingPage { Key = "screensaver", Title = "Screensaver", Glyph = "", Group = "General", Description = "Crabs that scuttle across idle terminals, like MobaXterm's penguins." }.Add(
            new ToggleItem("Crab screensaver", "After a while without keyboard or mouse input in FluenTTY, little Claude Code crabs come out on the terminal tabs that are showing (SSH, Telnet, Rlogin, Rsh, raw and serial). Any key, click or mouse movement sends them away; that key or click isn't passed on.",
                () => S.Interface.Screensaver, v => S.Interface.Screensaver = v, "consolesaver penguins idle animation crab"),
            new NumberItem("Start after", "Minutes without input in any FluenTTY window.", () => S.Interface.ScreensaverMinutes, v => S.Interface.ScreensaverMinutes = (int)v, 1, 240, "minutes", keywords: "TimerConst idle timeout screensaver"),
            new NumberItem("Number of crabs", "", () => S.Interface.ScreensaverCrabs, v => S.Interface.ScreensaverCrabs = (int)v, 1, 30, "crabs", keywords: "NbPenguins screensaver"),
            new ChoiceItem<CrabSpeed>("Speed", "", () => S.Interface.ScreensaverSpeed, v => S.Interface.ScreensaverSpeed = v,
                O((CrabSpeed.Slow, "Slow"), (CrabSpeed.Normal, "Normal"), (CrabSpeed.Fast, "Fast")), "TimerInt screensaver crabs"),
            new ChoiceItem<CrabSize>("Crab size", "", () => S.Interface.ScreensaverCrabSize, v => S.Interface.ScreensaverCrabSize = v,
                O((CrabSize.Small, "Small"), (CrabSize.Medium, "Medium"), (CrabSize.Large, "Large")), "screensaver crabs"),
            new ToggleItem("Keep sessions alive while the crabs are out", "Every minute, SSH sessions get an SSH \"ignore\" message and Telnet sessions a NOP, so servers and firewalls don't drop them as idle. Nothing reaches the remote shell. Rlogin, Rsh, raw and serial have no such no-op. For keepalives all the time, use Connection › Seconds between keepalives.",
                () => S.Interface.ScreensaverKeepalive, v => S.Interface.ScreensaverKeepalive = v, "keepalive anti-idle ping screensaver"),
            new ActionItem("Preview", "Brings the crabs out now on the terminal tabs that are showing. Press a key or click to send them away.", "Preview", w =>
            {
                if (!Screensaver.Preview())
                    FluentDialog.Info(w, "Screensaver", "Open a terminal tab first (SSH, Telnet, serial…): the crabs only come out on terminals.");
            }, keywords: "screensaver crabs test")));

        // ============================================================ Terminal
        pages.Add(new SettingPage { Key = "terminal", Title = "Terminal", Glyph = "", Group = "Terminal", Description = "Terminal emulation — PuTTY's Terminal panel." }.Add(
            new NumberItem("Scrollback lines", "Lines kept above the screen. PuTTY: Window › Lines of scrollback.", () => S.Terminal.ScrollbackLines, v => S.Terminal.ScrollbackLines = (int)v, 0, 1_000_000, "lines", keywords: "savelines history buffer"),
            new ToggleItem("Push erased text into the scrollback", "A full-screen clear keeps what was on screen.", () => S.Terminal.EraseToScrollback, v => S.Terminal.EraseToScrollback = v, "erase_to_scrollback"),
            new ToggleItem("Jump to the bottom on keypress", "", () => S.Terminal.ScrollOnKeypress, v => S.Terminal.ScrollOnKeypress = v, "scroll_on_key reset scrollback"),
            new ToggleItem("Jump to the bottom on new output", "", () => S.Terminal.ScrollOnOutput, v => S.Terminal.ScrollOnOutput = v, "scroll_on_disp"),
            new ToggleItem("Mouse wheel sends arrow keys in full-screen apps", "In less, man, vim etc. (the alternate screen) when mouse reporting is off.", () => S.Terminal.WheelSendsArrowsInAltScreen, v => S.Terminal.WheelSendsArrowsInAltScreen = v, "alternate scroll"),
            new HeaderItem("Emulation"),
            new ToggleItem("Auto wrap mode initially on", "", () => S.Terminal.AutoWrap, v => S.Terminal.AutoWrap = v, "wrap_mode DECAWM"),
            new ToggleItem("DEC Origin Mode initially on", "", () => S.Terminal.DecOriginMode, v => S.Terminal.DecOriginMode = v, "dec_om DECOM"),
            new ToggleItem("Implicit CR in every LF", "", () => S.Terminal.LfImpliesCr, v => S.Terminal.LfImpliesCr = v, "lfhascr newline"),
            new ToggleItem("Implicit LF in every CR", "", () => S.Terminal.CrImpliesLf, v => S.Terminal.CrImpliesLf = v, "crhaslf"),
            new ToggleItem("Use background colour to erase the screen", "", () => S.Terminal.BackgroundColourErase, v => S.Terminal.BackgroundColourErase = v, "bce"),
            new ToggleItem("Enable blinking text", "", () => S.Terminal.BlinkText, v => S.Terminal.BlinkText = v, "blinktext"),
            new TextItem("Answerback to ^E", "Sent when the host sends ENQ.", () => S.Terminal.Answerback, v => S.Terminal.Answerback = v, keywords: "answerback enq"),
            new HeaderItem("Line discipline"),
            new ChoiceItem<AutoOnOff>("Local echo", "Auto lets the protocol decide (on for Raw, negotiated for Telnet, off for SSH).", () => S.Terminal.LocalEcho, v => S.Terminal.LocalEcho = v,
                O((AutoOnOff.Auto, "Auto"), (AutoOnOff.On, "Force on"), (AutoOnOff.Off, "Force off")), "localecho"),
            new ChoiceItem<AutoOnOff>("Local line editing", "Edit a line locally and send it when you press Enter.", () => S.Terminal.LocalLineEditing, v => S.Terminal.LocalLineEditing = v,
                O((AutoOnOff.Auto, "Auto"), (AutoOnOff.On, "Force on"), (AutoOnOff.Off, "Force off")), "localedit")));

        pages.Add(new SettingPage { Key = "keyboard", Title = "Keyboard", Glyph = "", Group = "Terminal", Description = "What keys send — PuTTY's Terminal › Keyboard." }.Add(
            new ChoiceItem<BackspaceKey>("The Backspace key", "", () => S.Keyboard.Backspace, v => S.Keyboard.Backspace = v,
                O((BackspaceKey.ControlQuestion, "Control-? (127, DEL)"), (BackspaceKey.ControlH, "Control-H (8)")), "bksp_is_delete"),
            new ChoiceItem<HomeEndKeys>("The Home and End keys", "", () => S.Keyboard.HomeEnd, v => S.Keyboard.HomeEnd = v,
                O((HomeEndKeys.Xterm, "xterm (ESC [H / ESC [F)"), (HomeEndKeys.Standard, "Standard (ESC [1~ / ESC [4~, PuTTY's default)"), (HomeEndKeys.Rxvt, "rxvt")), "rxvt_homeend"),
            new ChoiceItem<FunctionKeys>("The function keys and keypad", "", () => S.Keyboard.FunctionKeys, v => S.Keyboard.FunctionKeys = v,
                O((FunctionKeys.Xterm216, "Xterm 216+ (modern xterm)"), (FunctionKeys.Tilde, "ESC[n~ (PuTTY's default)"), (FunctionKeys.Linux, "Linux"), (FunctionKeys.XtermR6, "Xterm R6"),
                  (FunctionKeys.Vt400, "VT400"), (FunctionKeys.Vt100Plus, "VT100+"), (FunctionKeys.Sco, "SCO")), "funky_type"),
            new ChoiceItem<ShiftedArrowKeys>("Shift/Ctrl/Alt with the arrow keys", "", () => S.Keyboard.ShiftedArrows, v => S.Keyboard.ShiftedArrows = v,
                O((ShiftedArrowKeys.XtermModifiers, "xterm-style modifier codes"), (ShiftedArrowKeys.CtrlTogglesApplicationMode, "Ctrl toggles app mode")), "sharrow_type"),
            new ToggleItem("Cursor keys start in application mode", "", () => S.Keyboard.ApplicationCursorKeys, v => S.Keyboard.ApplicationCursorKeys = v, "app_cursor"),
            new ChoiceItem<InitialKeypad>("Numeric keypad starts in", "", () => S.Keyboard.Keypad, v => S.Keyboard.Keypad = v,
                O((InitialKeypad.Normal, "Normal"), (InitialKeypad.Application, "Application"), (InitialKeypad.NetHack, "NetHack")), "app_keypad nethack_keypad"),
            new HeaderItem("Modifiers"),
            new ToggleItem("Control-Alt is different from AltGr", "Off: Ctrl+Alt combinations that type a character (AltGr) are sent as that character.", () => !S.Keyboard.CtrlAltIsAltGr, v => S.Keyboard.CtrlAltIsAltGr = !v, "ctrlaltkeys altgr"),
            new ToggleItem("Alt sends Escape (meta)", "Alt+key sends ESC then the key.", () => S.Keyboard.AltSendsEscape, v => S.Keyboard.AltSendsEscape = v, "meta"),
            new ToggleItem("Alt+F4 closes the window", "Off sends it to the remote side.", () => S.Keyboard.AltF4Closes, v => S.Keyboard.AltF4Closes = v, "alt_f4"),
            new ToggleItem("Alt+Space opens the window menu", "", () => S.Keyboard.AltSpaceSystemMenu, v => S.Keyboard.AltSpaceSystemMenu = v, "alt_space"),
            new HeaderItem("Shortcuts"),
            new ToggleItem("Ctrl+Shift+C / Ctrl+Shift+V copy and paste", "", () => S.Keyboard.CtrlShiftCopyPaste, v => S.Keyboard.CtrlShiftCopyPaste = v, "ctrlshiftcv clipboard"),
            new ToggleItem("Ctrl+Insert / Shift+Insert copy and paste", "", () => S.Keyboard.CtrlInsertShiftInsert, v => S.Keyboard.CtrlInsertShiftInsert = v, "ctrlshiftins"),
            new ToggleItem("Tab shortcuts", "Ctrl+Shift+T/W/D/N/P, Ctrl+Tab, Alt+1…9.", () => S.Keyboard.TabShortcuts, v => S.Keyboard.TabShortcuts = v),
            new ToggleItem("Ctrl+mouse wheel zooms the text", "", () => S.Keyboard.CtrlWheelZoom, v => S.Keyboard.CtrlWheelZoom = v, "font size zoom")));

        pages.Add(new SettingPage { Key = "bell", Title = "Bell", Glyph = "", Group = "Terminal", Description = "What happens on ^G — PuTTY's Terminal › Bell." }.Add(
            new ChoiceItem<BellStyle>("Action on bell", "", () => S.Bell.Style, v => S.Bell.Style = v,
                O((BellStyle.SystemDefault, "Default system alert sound"), (BellStyle.Visual, "Visual bell (flash the terminal)"), (BellStyle.WaveFile, "Play a sound file"), (BellStyle.Beep, "Beep"), (BellStyle.None, "None")), "beep"),
            new PathItem("Sound file", "Used by \"Play a sound file\".", () => S.Bell.WaveFile, v => S.Bell.WaveFile = v, "Wave files (*.wav)|*.wav|All files|*.*", keywords: "bell_wavefile"),
            new ChoiceItem<TaskbarBell>("Taskbar indication", "When the window isn't in the foreground.", () => S.Bell.Taskbar, v => S.Bell.Taskbar = v,
                O((TaskbarBell.Flashing, "Flash"), (TaskbarBell.Steady, "Steady highlight"), (TaskbarBell.Disabled, "None")), "beep_ind"),
            new ToggleItem("Mark the tab", "Shows a bell on a tab that rang while you were elsewhere.", () => S.Bell.TabIndicator, v => S.Bell.TabIndicator = v),
            new HeaderItem("Overload protection"),
            new ToggleItem("Bell is temporarily disabled when over-used", "", () => S.Bell.OverloadProtection, v => S.Bell.OverloadProtection = v, "bellovl"),
            new NumberItem("Over-use means this many bells", "", () => S.Bell.OverloadCount, v => S.Bell.OverloadCount = (int)v, 2, 100, "bells", keywords: "bellovl_n"),
            new NumberItem("…in this many seconds", "", () => S.Bell.OverloadSeconds, v => S.Bell.OverloadSeconds = v, 0.5, 60, "s", integer: false, keywords: "bellovl_t"),
            new NumberItem("Seconds of silence to re-enable the bell", "", () => S.Bell.OverloadSilenceSeconds, v => S.Bell.OverloadSilenceSeconds = v, 0.5, 600, "s", integer: false, keywords: "bellovl_s")));

        pages.Add(new SettingPage { Key = "features", Title = "Features", Glyph = "", Group = "Terminal", Description = "Turn off terminal features the remote side can use — PuTTY's Terminal › Features." }.Add(
            new ToggleItem("Disable application cursor keys mode", "", () => S.Terminal.NoApplicationCursorKeys, v => S.Terminal.NoApplicationCursorKeys = v, "no_applic_c"),
            new ToggleItem("Disable application keypad mode", "", () => S.Terminal.NoApplicationKeypad, v => S.Terminal.NoApplicationKeypad = v, "no_applic_k"),
            new ToggleItem("Disable xterm-style mouse reporting", "", () => S.Terminal.NoMouseReporting, v => S.Terminal.NoMouseReporting = v, "no_mouse_rep"),
            new ToggleItem("Disable remote-controlled terminal resizing", "", () => S.Terminal.NoRemoteResize, v => S.Terminal.NoRemoteResize = v, "no_remote_resize"),
            new ToggleItem("Disable switching to the alternate screen", "", () => S.Terminal.NoAltScreen, v => S.Terminal.NoAltScreen = v, "no_alt_screen"),
            new ToggleItem("Disable remote-controlled window title changing", "", () => S.Terminal.NoRemoteWindowTitle, v => S.Terminal.NoRemoteWindowTitle = v, "no_remote_wintitle"),
            new ChoiceItem<TitleQueryAction>("Response to remote title query", "SECURITY: answering with the real title can be abused.", () => S.Terminal.TitleQuery, v => S.Terminal.TitleQuery = v,
                O((TitleQueryAction.Empty, "Empty string"), (TitleQueryAction.None, "None"), (TitleQueryAction.Title, "Window title")), "remote_qtitle_action"),
            new ToggleItem("Disable remote-controlled clearing of the scrollback", "", () => S.Terminal.NoRemoteClearScrollback, v => S.Terminal.NoRemoteClearScrollback = v, "no_remote_clearscroll"),
            new ToggleItem("Disable destructive backspace on server sending ^?", "", () => S.Terminal.NoDestructiveBackspace, v => S.Terminal.NoDestructiveBackspace = v, "no_dbackspace"),
            new ToggleItem("Disable remote-controlled character set configuration", "", () => S.Terminal.NoRemoteCharset, v => S.Terminal.NoRemoteCharset = v, "no_remote_charset"),
            new ToggleItem("Disable bracketed paste mode", "", () => S.Terminal.NoBracketedPaste, v => S.Terminal.NoBracketedPaste = v, "no_bracketed_paste"),
            new ToggleItem("Let the remote side set the clipboard (OSC 52)", "Off by default: a server could otherwise put text on your clipboard.", () => S.Terminal.AllowRemoteClipboardWrite, v => S.Terminal.AllowRemoteClipboardWrite = v, "osc52 clipboard")));

        // ============================================================ Appearance
        pages.Add(new SettingPage { Key = "text", Title = "Text and cursor", Glyph = "", Group = "Appearance", Description = "PuTTY's Window › Appearance." }.Add(
            new CustomItem("Font", "Fixed-pitch fonts installed on this PC. Cascadia Mono ships with Windows 11.", () => SettingsEditors.FontPicker(() => S.Appearance.FontFamily, v => S.Appearance.FontFamily = v), wide: false, keywords: "font face typeface"),
            new NumberItem("Font size", "Ctrl+mouse wheel zooms one tab.", () => S.Appearance.FontSize, v => S.Appearance.FontSize = v, 5, 72, "pt", integer: false, keywords: "font height"),
            new ChoiceItem<FontWeightKind>("Font weight", "", () => S.Appearance.FontWeight, v => S.Appearance.FontWeight = v,
                O((FontWeightKind.Light, "Light"), (FontWeightKind.SemiLight, "Semilight"), (FontWeightKind.Normal, "Normal"), (FontWeightKind.Medium, "Medium"), (FontWeightKind.SemiBold, "Semibold"), (FontWeightKind.Bold, "Bold"))),
            new ChoiceItem<BoldStyle>("Show bold text as", "", () => S.Appearance.BoldStyle, v => S.Appearance.BoldStyle = v,
                O((BoldStyle.Both, "Bold font and bright colour"), (BoldStyle.Font, "Bold font"), (BoldStyle.Colour, "Bright colour")), "bold_style"),
            new NumberItem("Line height", "1.0 is the font's natural spacing.", () => S.Appearance.LineHeight, v => S.Appearance.LineHeight = v, 0.8, 2.0, "×", integer: false, keywords: "line spacing"),
            new NumberItem("Padding", "Gap between the text and the edge of the tab.", () => S.Appearance.Padding, v => S.Appearance.Padding = v, 0, 40, "px", keywords: "window_border margin"),
            new HeaderItem("Cursor"),
            new ChoiceItem<CursorShape>("Cursor appearance", "", () => S.Appearance.CursorShape, v => S.Appearance.CursorShape = v,
                O((CursorShape.Block, "Block"), (CursorShape.Underline, "Underline"), (CursorShape.Bar, "Vertical line")), "cursor_type"),
            new ToggleItem("Cursor blinks", "", () => S.Appearance.CursorBlink, v => S.Appearance.CursorBlink = v, "blink_cur"),
            new HeaderItem("Window"),
            new ToggleItem("Show the scrollbar", "", () => S.Appearance.ShowScrollbar, v => S.Appearance.ShowScrollbar = v, "scrollbar"),
            new ToggleItem("Show the scrollbar in full screen", "", () => S.Appearance.ScrollbarInFullScreen, v => S.Appearance.ScrollbarInFullScreen = v, "scrollbar_in_fullscreen"),
            new ToggleItem("Hide the mouse pointer when typing", "", () => S.Appearance.HideMouseWhileTyping, v => S.Appearance.HideMouseWhileTyping = v, "hide_mouseptr")));

        var schemeOptions = () => SettingsStore.AllSchemes.Select(s => (s.Name, s.Name)).ToArray();
        pages.Add(new SettingPage { Key = "colours", Title = "Colours", Glyph = "", Group = "Appearance", Description = "Colour schemes — PuTTY's Window › Colours." }.Add(
            new ChoiceItem<string>("Colour scheme in dark mode", "", () => S.Appearance.SchemeDark, v => S.Appearance.SchemeDark = v, schemeOptions(), "palette theme"),
            new ChoiceItem<string>("Colour scheme in light mode", "", () => S.Appearance.SchemeLight, v => S.Appearance.SchemeLight = v, schemeOptions(), "palette theme"),
            new NumberItem("Background opacity", "Below 100% lets the window material show through the terminal.", () => S.Appearance.BackgroundOpacity, v => S.Appearance.BackgroundOpacity = (int)v, 0, 100, "%", keywords: "transparency translucent"),
            new ToggleItem("Allow terminal to specify ANSI colours", "", () => S.Appearance.AnsiColour, v => S.Appearance.AnsiColour = v, "ansi_colour"),
            new ToggleItem("Allow terminal to use xterm 256-colour mode", "", () => S.Appearance.Xterm256Colour, v => S.Appearance.Xterm256Colour = v, "xterm_256_colour"),
            new ToggleItem("Allow terminal to use 24-bit colours", "", () => S.Appearance.TrueColour, v => S.Appearance.TrueColour = v, "true_colour truecolor"),
            new CustomItem("Edit colour schemes", "Duplicate a built-in scheme to customise it.", SettingsEditors.SchemeEditor, keywords: "colours palette ansi foreground background cursor")));

        pages.Add(new SettingPage { Key = "selection", Title = "Selection and clipboard", Glyph = "", Group = "Appearance", Description = "Mouse buttons, copy and paste — PuTTY's Window › Selection." }.Add(
            new ChoiceItem<RightClickAction>("Right mouse button", "Ctrl+right-click always opens the menu.", () => S.Selection.RightClick, v => S.Selection.RightClick = v,
                O((RightClickAction.Paste, "Paste (PuTTY)"), (RightClickAction.ContextMenu, "Show the menu"), (RightClickAction.ExtendSelection, "Extend the selection (xterm)")), "mouse_is_xterm"),
            new ChoiceItem<MiddleClickAction>("Middle mouse button", "", () => S.Selection.MiddleClick, v => S.Selection.MiddleClick = v,
                O((MiddleClickAction.ExtendSelection, "Extend the selection"), (MiddleClickAction.Paste, "Paste"), (MiddleClickAction.None, "Nothing"))),
            new ToggleItem("Copy selected text automatically", "", () => S.Selection.CopyOnSelect, v => S.Selection.CopyOnSelect = v, "mouseautocopy"),
            new ToggleItem("Alt+drag selects a rectangle", "", () => S.Selection.AltDragRectangular, v => S.Selection.AltDragRectangular = v, "rect_select block"),
            new ToggleItem("Shift overrides the application's use of the mouse", "", () => S.Selection.ShiftOverridesMouseReporting, v => S.Selection.ShiftOverridesMouseReporting = v, "mouse_override"),
            new TextItem("Word separators", "Characters that end a word when double-clicking.", () => S.Selection.WordDelimiters, v => S.Selection.WordDelimiters = v, mono: true, keywords: "wordness"),
            new ToggleItem("Trim trailing spaces when copying", "", () => S.Selection.TrimTrailingWhitespace, v => S.Selection.TrimTrailingWhitespace = v),
            new HeaderItem("Paste"),
            new ToggleItem("Permit control characters in pasted text", "", () => S.Selection.PasteControlCharacters, v => S.Selection.PasteControlCharacters = v, "paste_controls"),
            new ToggleItem("Confirm before pasting multiple lines", "", () => S.Selection.WarnMultiLinePaste, v => S.Selection.WarnMultiLinePaste = v),
            new ToggleItem("Confirm before pasting more than 5 KB", "", () => S.Selection.WarnLargePaste, v => S.Selection.WarnLargePaste = v)));

        pages.Add(new SettingPage { Key = "translation", Title = "Character set", Glyph = "", Group = "Appearance", Description = "PuTTY's Window › Translation." }.Add(
            new ChoiceItem<string>("Remote character set", "How bytes from the host are decoded. Sessions can override it.", () => S.Translation.RemoteCharset, v => S.Translation.RemoteCharset = v,
                TerminalConfig.Charsets.Select(c => (c, c)).ToArray(), "line_codepage encoding codepage utf-8"),
            new ToggleItem("Treat CJK ambiguous characters as wide", "", () => S.Translation.CjkAmbiguousWide, v => S.Translation.CjkAmbiguousWide = v, "cjk_ambig_wide"),
            new ToggleItem("Allow the host to switch to UTF-8 (ESC % G)", "", () => S.Translation.Utf8Override, v => S.Translation.Utf8Override = v, "utf8_override"),
            new ChoiceItem<LineDrawing>("Line drawing characters", "", () => S.Translation.LineDrawing, v => S.Translation.LineDrawing = v,
                O((LineDrawing.Unicode, "Unicode box drawing"), (LineDrawing.Ascii, "Poor man's (+, -, |)")), "vtmode")));

        // ============================================================ Connection
        pages.Add(new SettingPage { Key = "connection", Title = "Connection", Glyph = "", Group = "Connection", Description = "PuTTY's Connection panel." }.Add(
            new NumberItem("Connection timeout", "", () => S.Connection.ConnectTimeoutSeconds, v => S.Connection.ConnectTimeoutSeconds = (int)v, 3, 300, "seconds"),
            new NumberItem("Seconds between keepalives", "0 turns them off. Sessions can override it.", () => S.Connection.KeepaliveSeconds, v => S.Connection.KeepaliveSeconds = (int)v, 0, 3600, "seconds", keywords: "ping_interval keep alive"),
            new ToggleItem("Disable Nagle's algorithm", "", () => S.Connection.TcpNoDelay, v => S.Connection.TcpNoDelay = v, "tcp_nodelay"),
            new ToggleItem("Enable TCP keepalives", "", () => S.Connection.TcpKeepalives, v => S.Connection.TcpKeepalives = v, "tcp_keepalives SO_KEEPALIVE"),
            new ChoiceItem<AddressFamilyChoice>("Internet protocol version", "", () => S.Connection.AddressFamily, v => S.Connection.AddressFamily = v,
                O((AddressFamilyChoice.Auto, "Auto"), (AddressFamilyChoice.IPv4, "IPv4"), (AddressFamilyChoice.IPv6, "IPv6")), "addressfamily ipv6")));

        pages.Add(new SettingPage { Key = "data", Title = "Data sent to the server", Glyph = "", Group = "Connection", Description = "PuTTY's Connection › Data." }.Add(
            new TextItem("Auto-login username", "Used when a session doesn't set one.", () => S.Connection.AutoLoginUsername, v => S.Connection.AutoLoginUsername = v, keywords: "username login"),
            new ToggleItem("Use the Windows user name", "", () => S.Connection.UseWindowsUsername, v => S.Connection.UseWindowsUsername = v, "username_from_env"),
            new TextItem("Terminal-type string", "", () => S.Connection.TerminalType, v => S.Connection.TerminalType = v, keywords: "termtype TERM xterm"),
            new TextItem("Terminal speeds", "", () => S.Connection.TerminalSpeed, v => S.Connection.TerminalSpeed = v, keywords: "termspeed"),
            new CustomItem("Environment variables", "Sent to Telnet servers (NEW-ENVIRON). SSH.NET can't send SSH environment requests.", () => SettingsEditors.Environment(() => S.Connection.Environment), keywords: "environmt")));

        pages.Add(new SettingPage { Key = "proxy", Title = "Proxy", Glyph = "", Group = "Connection", Description = "PuTTY's Connection › Proxy. Used by SSH, SFTP, Telnet, Rlogin, Raw and VNC sessions." }.Add(
            new ChoiceItem<ProxyKind>("Proxy type", "", () => S.Proxy.Type, v => S.Proxy.Type = v,
                O((ProxyKind.None, "None"), (ProxyKind.Socks4, "SOCKS 4"), (ProxyKind.Socks5, "SOCKS 5"), (ProxyKind.Http, "HTTP CONNECT")), "proxy_type socks"),
            new TextItem("Proxy host name", "", () => S.Proxy.Host, v => S.Proxy.Host = v),
            new NumberItem("Proxy port", "", () => S.Proxy.Port, v => S.Proxy.Port = (int)v, 1, 65535),
            new TextItem("Proxy user name", "", () => S.Proxy.Username, v => S.Proxy.Username = v),
            new PasswordItem("Proxy password", "Encrypted for your Windows account.", () => S.Proxy.Password, v => S.Proxy.Password = v),
            new TextItem("Exclude hosts/IPs", "Comma separated: names, *.domain, .domain, CIDR ranges.", () => S.Proxy.ExcludeList, v => S.Proxy.ExcludeList = v, keywords: "proxy_exclude_list"),
            new ToggleItem("Consider proxying local host connections", "", () => S.Proxy.ProxyLocalhost, v => S.Proxy.ProxyLocalhost = v, "even_proxy_localhost"),
            new ChoiceItem<AutoOnOff>("Do DNS name lookup at the proxy end", "", () => S.Proxy.DnsAtProxy, v => S.Proxy.DnsAtProxy = v,
                O((AutoOnOff.Auto, "Auto"), (AutoOnOff.On, "Yes"), (AutoOnOff.Off, "No")), "proxy_dns")));

        pages.Add(new SettingPage { Key = "logging", Title = "Session logging", Glyph = "", Group = "Connection", Description = "PuTTY's Session › Logging." }.Add(
            new ChoiceItem<LogMode>("Session logging", "Sessions can override it.", () => S.Logging.Mode, v => S.Logging.Mode = v,
                O((LogMode.Off, "None"), (LogMode.Printable, "Printable output"), (LogMode.AllOutput, "All session output")), "logtype"),
            new PathItem("Log file name", "&H host, &P port, &S session, &Y &M &D date, &T time.", () => S.Logging.FileName, v => S.Logging.FileName = v, "Log files (*.log)|*.log|All files|*.*", save: true, keywords: "logfilename"),
            new ChoiceItem<LogFileExists>("When the log file already exists", "", () => S.Logging.WhenExists, v => S.Logging.WhenExists = v,
                O((LogFileExists.Append, "Append to the end"), (LogFileExists.Overwrite, "Overwrite"), (LogFileExists.Ask, "Ask each time")), "logxfovr"),
            new ToggleItem("Flush the log frequently", "", () => S.Logging.Flush, v => S.Logging.Flush = v, "logflush"),
            new ToggleItem("Include a header", "", () => S.Logging.Header, v => S.Logging.Header = v, "logheader"),
            new ToggleItem("Timestamp each line (printable output)", "", () => S.Logging.Timestamps, v => S.Logging.Timestamps = v)));

        // ============================================================ SSH
        var algos = SshConnector.DefaultAlgorithms();
        pages.Add(new SettingPage { Key = "ssh", Title = "SSH", Glyph = "", Group = "SSH", Description = "PuTTY's Connection › SSH." }.Add(
            new ToggleItem("Enable compression", "", () => S.Ssh.Compression, v => S.Ssh.Compression = v, "compression zlib"),
            new TextItem("Initial commands", "Typed after login (PuTTY's \"Remote command\"). Sessions can set their own.", () => S.Ssh.InitialCommand, v => S.Ssh.InitialCommand = v, multiline: true, mono: true, keywords: "remote_cmd"),
            new ToggleItem("Don't start a shell or command at all", "For tunnels only.", () => S.Ssh.NoShell, v => S.Ssh.NoShell = v, "ssh_no_shell"),
            new ToggleItem("Don't allocate a pseudo-terminal", "", () => S.Ssh.NoPty, v => S.Ssh.NoPty = v, "nopty"),
            new ToggleItem("Show the server's login banner", "", () => S.Ssh.ShowBanner, v => S.Ssh.ShowBanner = v, "ssh_show_banner"),
            new HeaderItem("SFTP browser"),
            new ToggleItem("Open an SFTP browser beside SSH terminals", "Sessions can override it. Double-click a file to edit it.", () => S.Ssh.OpenSftpBrowser, v => S.Ssh.OpenSftpBrowser = v, "mobaxterm file browser"),
            new ToggleItem("Follow the terminal's folder", "Needs the shell to report its directory (OSC 7, e.g. VTE's vte.sh).", () => S.Ssh.SftpFollowTerminalFolder, v => S.Ssh.SftpFollowTerminalFolder = v)));

        pages.Add(new SettingPage { Key = "auth", Title = "Authentication", Glyph = "", Group = "SSH", Description = "PuTTY's SSH › Auth. Prompts appear in the terminal, like PuTTY's." }.Add(
            new PathItem("Default private key file", "PuTTY .ppk (v2/v3), OpenSSH or PEM. Sessions can set their own.", () => S.Ssh.PrivateKeyFile, v => S.Ssh.PrivateKeyFile = v,
                "Private keys (*.ppk;id_*;*.pem;*.key)|*.ppk;id_*;*.pem;*.key|All files|*.*", keywords: "keyfile ppk"),
            new ToggleItem("Attempt keyboard-interactive auth", "", () => S.Ssh.TryKeyboardInteractive, v => S.Ssh.TryKeyboardInteractive = v, "try_ki_auth 2fa otp"),
            new ToggleItem("Attempt password auth", "", () => S.Ssh.TryPassword, v => S.Ssh.TryPassword = v),
            new NumberItem("Password attempts", "", () => S.Ssh.MaxAuthAttempts, v => S.Ssh.MaxAuthAttempts = (int)v, 1, 10),
            new NoteItem("Pageant/agent authentication, GSSAPI (Kerberos) and X11 forwarding aren't available: the SSH library FluenTTY uses (SSH.NET) doesn't implement them. For graphical Linux apps use an XDMCP session.")));

        pages.Add(new SettingPage { Key = "algorithms", Title = "Algorithms", Glyph = "", Group = "SSH", Description = "Preference order, most preferred first — PuTTY's SSH › Kex / Host keys / Cipher." }.Add(
            new CustomItem("Key exchange algorithms", "", () => SettingsEditors.AlgorithmList(() => S.Ssh.KexAlgorithms, v => S.Ssh.KexAlgorithms = v, algos.kex), keywords: "kex ssh_kexlist diffie-hellman curve25519"),
            new CustomItem("Host key algorithms", "", () => SettingsEditors.AlgorithmList(() => S.Ssh.HostKeyAlgorithms, v => S.Ssh.HostKeyAlgorithms = v, algos.hostKeys), keywords: "ssh_hklist ed25519 rsa"),
            new ToggleItem("Prefer algorithms with a known host key", "Avoids a \"new key\" warning when the server offers a type you haven't seen.", () => S.Ssh.PreferKnownHostKeys, v => S.Ssh.PreferKnownHostKeys = v, "ssh_prefer_known_hostkeys"),
            new CustomItem("Encryption ciphers", "", () => SettingsEditors.AlgorithmList(() => S.Ssh.Ciphers, v => S.Ssh.Ciphers = v, algos.ciphers), keywords: "ssh_cipherlist aes chacha20"),
            new CustomItem("MAC algorithms", "", () => SettingsEditors.AlgorithmList(() => S.Ssh.Macs, v => S.Ssh.Macs = v, algos.macs), keywords: "hmac")));

        pages.Add(new SettingPage { Key = "hostkeys", Title = "Host keys", Glyph = "", Group = "SSH", Description = "Servers whose keys you've accepted (PuTTY keeps these in SshHostKeys)." }.Add(
            new CustomItem("Known host keys", "", SettingsEditors.KnownHosts, keywords: "known_hosts fingerprint"),
            new CustomItem("Manually configured host keys", "Accepted for any host without asking (SHA256 fingerprints).", () => SettingsEditors.StringList(() => S.Ssh.ManualHostKeys, "SHA256 fingerprint"), keywords: "ssh_manual_hostkeys")));

        pages.Add(new SettingPage { Key = "tunnels", Title = "Tunnels", Glyph = "", Group = "SSH", Description = "Port forwarding for every SSH session — PuTTY's SSH › Tunnels. Sessions can add their own." }.Add(
            new ToggleItem("Local ports accept connections from other hosts", "", () => S.Ssh.LocalPortsAcceptAll, v => S.Ssh.LocalPortsAcceptAll = v, "lport_acceptall"),
            new ToggleItem("Remote ports do the same (SSH-2 only)", "", () => S.Ssh.RemotePortsAcceptAll, v => S.Ssh.RemotePortsAcceptAll = v, "rport_acceptall"),
            new CustomItem("Forwarded ports", "Local: this PC's port reaches the destination via the server. Remote: the server's port reaches the destination via this PC. Dynamic: a SOCKS proxy.", () => SettingsEditors.PortForwards(() => S.Ssh.PortForwards), keywords: "portfwd forwarding socks")));

        // ============================================================ Other protocols
        pages.Add(new SettingPage { Key = "serial", Title = "Serial", Glyph = "", Group = "Other protocols", Description = "Defaults for COM port sessions — PuTTY's Connection › Serial." }.Add(
            new TextItem("Serial line to connect to", "", () => S.Serial.Line, v => S.Serial.Line = v, keywords: "serline com port"),
            new NumberItem("Speed (baud)", "", () => S.Serial.Speed, v => S.Serial.Speed = (int)v, 50, 4_000_000, "baud", keywords: "serspeed baud rate"),
            new NumberItem("Data bits", "", () => S.Serial.DataBits, v => S.Serial.DataBits = (int)v, 5, 8, keywords: "serdatabits"),
            new ChoiceItem<SerialStopBits>("Stop bits", "", () => S.Serial.StopBits, v => S.Serial.StopBits = v, O((SerialStopBits.One, "1"), (SerialStopBits.OnePointFive, "1.5"), (SerialStopBits.Two, "2")), "serstopbits"),
            new ChoiceItem<SerialParity>("Parity", "", () => S.Serial.Parity, v => S.Serial.Parity = v,
                O((SerialParity.None, "None"), (SerialParity.Odd, "Odd"), (SerialParity.Even, "Even"), (SerialParity.Mark, "Mark"), (SerialParity.Space, "Space")), "serparity"),
            new ChoiceItem<SerialFlow>("Flow control", "", () => S.Serial.FlowControl, v => S.Serial.FlowControl = v,
                O((SerialFlow.XonXoff, "XON/XOFF"), (SerialFlow.None, "None"), (SerialFlow.RtsCts, "RTS/CTS"), (SerialFlow.DsrDtr, "DSR/DTR")), "serflow handshake"),
            new ToggleItem("Assert DTR", "Some devices need it high to talk.", () => S.Serial.DtrEnable, v => S.Serial.DtrEnable = v),
            new ToggleItem("Assert RTS", "When flow control isn't RTS/CTS.", () => S.Serial.RtsEnable, v => S.Serial.RtsEnable = v),
            new NumberItem("Break length", "Special commands › Break.", () => S.Serial.BreakMilliseconds, v => S.Serial.BreakMilliseconds = (int)v, 10, 5000, "ms")));

        pages.Add(new SettingPage { Key = "telnet", Title = "Telnet", Glyph = "", Group = "Other protocols", Description = "PuTTY's Connection › Telnet." }.Add(
            new ToggleItem("Handle OLD_ENVIRON the RFC way", "Off: BSD servers' swapped VAR/VALUE codes.", () => S.Telnet.RfcEnvironment, v => S.Telnet.RfcEnvironment = v, "rfc_environ"),
            new ToggleItem("Passive negotiation", "Wait for the server to start option negotiation.", () => S.Telnet.PassiveNegotiation, v => S.Telnet.PassiveNegotiation = v, "passive_telnet"),
            new ToggleItem("Keyboard sends Telnet special commands", "Backspace → Erase Character, Ctrl+C → Interrupt Process, Ctrl+Z → Suspend.", () => S.Telnet.KeyboardSendsTelnetCommands, v => S.Telnet.KeyboardSendsTelnetCommands = v, "telnet_keyboard"),
            new ToggleItem("Return key sends Telnet New Line instead of ^M", "", () => S.Telnet.ReturnSendsTelnetNewline, v => S.Telnet.ReturnSendsTelnetNewline = v, "telnet_newline")));

        pages.Add(new SettingPage { Key = "rlogin", Title = "Rlogin and Rsh", Glyph = "", Group = "Other protocols", Description = "PuTTY's Connection › Rlogin." }.Add(
            new TextItem("Local username", "Empty uses your Windows user name.", () => S.Rlogin.LocalUsername, v => S.Rlogin.LocalUsername = v, keywords: "localusername"),
            new TextItem("Default rsh command", "Rsh runs one command. Empty asks when connecting.", () => S.Rlogin.RshCommand, v => S.Rlogin.RshCommand = v, mono: true)));

        // ============================================================ Remote desktop
        pages.Add(new SettingPage { Key = "rdp", Title = "Remote Desktop (RDP)", Glyph = "", Group = "Remote desktop", Description = "Uses the Remote Desktop client built into Windows (the same engine as mstsc)." }.Add(
            new HeaderItem("Display"),
            new ChoiceItem<RdpSizeMode>("Desktop size", "", () => S.Rdp.SizeMode, v => S.Rdp.SizeMode = v, O((RdpSizeMode.FitToWindow, "Fit to the tab"), (RdpSizeMode.Fixed, "Fixed size")), "resolution"),
            new NumberItem("Fixed width", "", () => S.Rdp.Width, v => S.Rdp.Width = (int)v, 200, 8192, "px"),
            new NumberItem("Fixed height", "", () => S.Rdp.Height, v => S.Rdp.Height = (int)v, 200, 8192, "px"),
            new ChoiceItem<RdpResizeBehavior>("When the tab is resized", "", () => S.Rdp.ResizeBehavior, v => S.Rdp.ResizeBehavior = v,
                O((RdpResizeBehavior.DynamicResolution, "Change the remote resolution"), (RdpResizeBehavior.SmartSizing, "Scale the picture"), (RdpResizeBehavior.Scrollbars, "Keep the size"))),
            new ChoiceItem<int>("Colours", "", () => S.Rdp.ColorDepth, v => S.Rdp.ColorDepth = v, O((32, "Highest quality (32 bit)"), (24, "True colour (24 bit)"), (16, "High colour (16 bit)"), (15, "High colour (15 bit)")), "color depth"),
            new ToggleItem("Show the connection bar in full screen", "", () => S.Rdp.ConnectionBarInFullScreen, v => S.Rdp.ConnectionBarInFullScreen = v),
            new HeaderItem("Local resources"),
            new ChoiceItem<RdpAudio>("Remote audio playback", "", () => S.Rdp.Audio, v => S.Rdp.Audio = v,
                O((RdpAudio.PlayOnThisComputer, "Play on this computer"), (RdpAudio.PlayOnRemoteComputer, "Play on the remote computer"), (RdpAudio.DoNotPlay, "Do not play"))),
            new ToggleItem("Record from this computer's microphone", "", () => S.Rdp.AudioCapture, v => S.Rdp.AudioCapture = v, "audio capture"),
            new ChoiceItem<RdpKeyboardHook>("Apply Windows key combinations", "Alt+Tab, Win key etc.", () => S.Rdp.KeyboardHook, v => S.Rdp.KeyboardHook = v,
                O((RdpKeyboardHook.OnlyInFullScreen, "Only when using the full screen"), (RdpKeyboardHook.OnRemoteComputer, "On the remote computer"), (RdpKeyboardHook.OnThisComputer, "On this computer"))),
            new ToggleItem("Clipboard", "", () => S.Rdp.RedirectClipboard, v => S.Rdp.RedirectClipboard = v, "redirect"),
            new ToggleItem("Drives", "", () => S.Rdp.RedirectDrives, v => S.Rdp.RedirectDrives = v, "redirect disk"),
            new ToggleItem("Printers", "", () => S.Rdp.RedirectPrinters, v => S.Rdp.RedirectPrinters = v, "redirect"),
            new ToggleItem("Smart cards", "", () => S.Rdp.RedirectSmartCards, v => S.Rdp.RedirectSmartCards = v, "redirect"),
            new ToggleItem("Ports", "", () => S.Rdp.RedirectPorts, v => S.Rdp.RedirectPorts = v, "redirect com"),
            new HeaderItem("Experience"),
            new ChoiceItem<RdpConnectionSpeed>("Connection speed", "", () => S.Rdp.ConnectionSpeed, v => S.Rdp.ConnectionSpeed = v,
                O((RdpConnectionSpeed.AutoDetect, "Detect automatically"), (RdpConnectionSpeed.Lan, "LAN (10 Mbps or higher)"), (RdpConnectionSpeed.Wan, "WAN"), (RdpConnectionSpeed.HighSpeedBroadband, "High-speed broadband"),
                  (RdpConnectionSpeed.Satellite, "Satellite"), (RdpConnectionSpeed.LowSpeedBroadband, "Low-speed broadband"), (RdpConnectionSpeed.Modem, "Modem"))),
            new ToggleItem("Desktop background", "", () => S.Rdp.DesktopBackground, v => S.Rdp.DesktopBackground = v, "wallpaper"),
            new ToggleItem("Font smoothing", "", () => S.Rdp.FontSmoothing, v => S.Rdp.FontSmoothing = v),
            new ToggleItem("Desktop composition", "", () => S.Rdp.DesktopComposition, v => S.Rdp.DesktopComposition = v),
            new ToggleItem("Show window contents while dragging", "", () => S.Rdp.ShowWindowContentsWhileDragging, v => S.Rdp.ShowWindowContentsWhileDragging = v),
            new ToggleItem("Menu and window animation", "", () => S.Rdp.MenuAnimations, v => S.Rdp.MenuAnimations = v),
            new ToggleItem("Visual styles", "", () => S.Rdp.VisualStyles, v => S.Rdp.VisualStyles = v, "themes"),
            new ToggleItem("Persistent bitmap caching", "", () => S.Rdp.PersistentBitmapCaching, v => S.Rdp.PersistentBitmapCaching = v),
            new ToggleItem("Compression", "", () => S.Rdp.Compression, v => S.Rdp.Compression = v),
            new ToggleItem("Reconnect if the connection drops", "", () => S.Rdp.AutoReconnect, v => S.Rdp.AutoReconnect = v, "auto reconnect"),
            new HeaderItem("Security"),
            new ToggleItem("Network Level Authentication (CredSSP)", "", () => S.Rdp.NetworkLevelAuthentication, v => S.Rdp.NetworkLevelAuthentication = v, "nla credssp"),
            new ChoiceItem<RdpServerAuth>("If server authentication fails", "", () => S.Rdp.ServerAuthentication, v => S.Rdp.ServerAuthentication = v,
                O((RdpServerAuth.WarnMe, "Warn me"), (RdpServerAuth.ConnectAnyway, "Connect and don't warn me"), (RdpServerAuth.DoNotConnect, "Do not connect")), "certificate"),
            new ToggleItem("Connect to the admin session", "mstsc /admin.", () => S.Rdp.AdminSession, v => S.Rdp.AdminSession = v, "console"),
            new HeaderItem("RD Gateway"),
            new ChoiceItem<RdpGatewayMode>("Use an RD Gateway", "", () => S.Rdp.GatewayMode, v => S.Rdp.GatewayMode = v,
                O((RdpGatewayMode.None, "No"), (RdpGatewayMode.Automatic, "Automatically detect"), (RdpGatewayMode.Always, "Always"))),
            new TextItem("Gateway server", "", () => S.Rdp.GatewayHost, v => S.Rdp.GatewayHost = v),
            new ToggleItem("Use my Remote Desktop credentials for the gateway", "", () => S.Rdp.GatewayUseSameCredentials, v => S.Rdp.GatewayUseSameCredentials = v)));

        pages.Add(new SettingPage { Key = "vnc", Title = "VNC", Glyph = "", Group = "Remote desktop", Description = "RFB 3.3–3.8 with VNC password authentication." }.Add(
            new ChoiceItem<VncEncoding>("Preferred encoding", "ZRLE is best on slow links; Raw needs no processing on fast LANs.", () => S.Vnc.Encoding, v => S.Vnc.Encoding = v,
                O((VncEncoding.Auto, "Automatic (ZRLE, Hextile, CopyRect, RRE, Raw)"), (VncEncoding.Zrle, "ZRLE"), (VncEncoding.Hextile, "Hextile"), (VncEncoding.Raw, "Raw"))),
            new ChoiceItem<VncColorDepth>("Colours", "", () => S.Vnc.ColorDepth, v => S.Vnc.ColorDepth = v,
                O((VncColorDepth.TrueColor, "True colour (24 bit)"), (VncColorDepth.HighColor, "High colour (16 bit)"), (VncColorDepth.Colors256, "256 colours (8 bit)"))),
            new ChoiceItem<VncScaling>("Scaling", "", () => S.Vnc.Scaling, v => S.Vnc.Scaling = v, O((VncScaling.FitToWindow, "Fit to the tab"), (VncScaling.ActualSize, "Actual size with scrollbars"))),
            new ToggleItem("Shared session", "Let other viewers stay connected.", () => S.Vnc.Shared, v => S.Vnc.Shared = v),
            new ToggleItem("View only", "Don't send keyboard or mouse input.", () => S.Vnc.ViewOnly, v => S.Vnc.ViewOnly = v),
            new ToggleItem("Share the clipboard", "Text copied on the remote computer arrives here. Yours is sent only when you paste into the session (Ctrl+V), because VNC isn't encrypted.", () => S.Vnc.ClipboardSharing, v => S.Vnc.ClipboardSharing = v),
            new ToggleItem("Use a crosshair pointer over the desktop", "The remote cursor is drawn by the server.", () => S.Vnc.ShowRemoteCursor, v => S.Vnc.ShowRemoteCursor = v)));

        pages.Add(new SettingPage { Key = "xdmcp", Title = "XDMCP", Glyph = "", Group = "Remote desktop", Description = "XDMCP needs an X server on this PC; FluenTTY starts it and shows it in the tab." }.Add(
            new NoteItem(XdmcpTab.FindXServer() is { } found ? $"X server found: {found}" : "No X server found. Install VcXsrv (free, open source) or Xming, or set the path below."),
            new PathItem("X server program", "Empty finds VcXsrv, Xming or Cygwin/X automatically.", () => S.Xdmcp.XServerPath, v => S.Xdmcp.XServerPath = v, "Programs (*.exe)|*.exe", keywords: "vcxsrv xming"),
            new ChoiceItem<XdmcpQuery>("Query", "", () => S.Xdmcp.QueryMode, v => S.Xdmcp.QueryMode = v,
                O((XdmcpQuery.Query, "Query the host"), (XdmcpQuery.Broadcast, "Broadcast on the local network"), (XdmcpQuery.Indirect, "Indirect via the host"))),
            new ChoiceItem<XWindowMode>("Show the session", "", () => S.Xdmcp.WindowMode, v => S.Xdmcp.WindowMode = v,
                O((XWindowMode.SingleWindow, "In the tab"), (XWindowMode.FullScreen, "Full screen (X server's own window)"))),
            new TextItem("Resolution", "Like 1600x900, or \"auto\" to match the tab.", () => S.Xdmcp.Resolution, v => S.Xdmcp.Resolution = v),
            new NumberItem("Display number", "0 picks the first free display.", () => S.Xdmcp.DisplayNumber, v => S.Xdmcp.DisplayNumber = (int)v, 0, 63),
            new ToggleItem("Share the clipboard", "", () => S.Xdmcp.ClipboardSync, v => S.Xdmcp.ClipboardSync = v),
            new ToggleItem("Stop the X server when the tab closes", "", () => S.Xdmcp.StopServerWithTab, v => S.Xdmcp.StopServerWithTab = v),
            new TextItem("Extra X server arguments", "", () => S.Xdmcp.ExtraArguments, v => S.Xdmcp.ExtraArguments = v, mono: true)));

        // ============================================================ Files
        pages.Add(new SettingPage { Key = "files", Title = "File transfer", Glyph = "", Group = "Files", Description = "SFTP and FTP browsers." }.Add(
            new ChoiceItem<FileDoubleClick>("Double-clicking a file", "", () => S.Files.DoubleClick, v => S.Files.DoubleClick = v,
                O((FileDoubleClick.Edit, "Edit it in FluenTTY"), (FileDoubleClick.OpenWithDefaultApp, "Open with the Windows default app (changes are uploaded)"), (FileDoubleClick.Download, "Download it"))),
            new NumberItem("Largest file to open in the editor", "", () => S.Files.MaxEditSizeMB, v => S.Files.MaxEditSizeMB = (int)v, 1, 500, "MB"),
            new PathItem("Download folder", "Empty uses your Downloads folder.", () => S.Files.DownloadFolder, v => S.Files.DownloadFolder = v, folder: true),
            new ToggleItem("Show hidden files", "Names starting with a dot.", () => S.Files.ShowHiddenFiles, v => S.Files.ShowHiddenFiles = v, "dotfiles"),
            new ToggleItem("Confirm before deleting", "", () => S.Files.ConfirmDelete, v => S.Files.ConfirmDelete = v),
            new ToggleItem("Confirm before overwriting", "", () => S.Files.ConfirmOverwrite, v => S.Files.ConfirmOverwrite = v),
            new ToggleItem("Keep modification times on download", "", () => S.Files.PreserveTimestamps, v => S.Files.PreserveTimestamps = v),
            new HeaderItem("FTP"),
            new ChoiceItem<FtpEncryption>("Encryption", "", () => S.Files.FtpEncryption, v => S.Files.FtpEncryption = v,
                O((FtpEncryption.Auto, "TLS; ask before connecting without it"), (FtpEncryption.Explicit, "Require explicit TLS (FTPES)"), (FtpEncryption.Implicit, "Implicit TLS (FTPS, port 990)"), (FtpEncryption.None, "None (password sent in plain text)")), "ftps ssl"),
            new ChoiceItem<FtpDataMode>("Data connection", "", () => S.Files.FtpDataMode, v => S.Files.FtpDataMode = v,
                O((FtpDataMode.AutoPassive, "Passive (automatic)"), (FtpDataMode.Passive, "PASV"), (FtpDataMode.ExtendedPassive, "EPSV"), (FtpDataMode.Active, "Active")), "pasv port"),
            new ChoiceItem<CertificatePolicy>("Untrusted TLS certificates", "", () => S.Files.Certificates, v => S.Files.Certificates = v,
                O((CertificatePolicy.Ask, "Ask"), (CertificatePolicy.AcceptAll, "Accept"), (CertificatePolicy.Strict, "Refuse"))),
            new ChoiceItem<string>("File name encoding", "", () => S.Files.FtpEncoding, v => S.Files.FtpEncoding = v, TerminalConfig.Charsets.Select(c => (c, c)).ToArray())));

        pages.Add(new SettingPage { Key = "editor", Title = "Editor", Glyph = "", Group = "Files", Description = "The editor that opens remote files." }.Add(
            new CustomItem("Font", "", () => SettingsEditors.FontPicker(() => S.Editor.FontFamily, v => S.Editor.FontFamily = v), wide: false),
            new NumberItem("Font size", "", () => S.Editor.FontSize, v => S.Editor.FontSize = v, 6, 48, "pt", integer: false),
            new ToggleItem("Word wrap", "", () => S.Editor.WordWrap, v => S.Editor.WordWrap = v),
            new ToggleItem("Line numbers", "", () => S.Editor.ShowLineNumbers, v => S.Editor.ShowLineNumbers = v),
            new ToggleItem("Highlight the current line", "", () => S.Editor.HighlightCurrentLine, v => S.Editor.HighlightCurrentLine = v),
            new ToggleItem("Syntax highlighting", "", () => S.Editor.SyntaxHighlighting, v => S.Editor.SyntaxHighlighting = v),
            new NumberItem("Tab size", "", () => S.Editor.TabSize, v => S.Editor.TabSize = (int)v, 1, 16, "spaces"),
            new ToggleItem("Insert spaces for Tab", "", () => S.Editor.ConvertTabsToSpaces, v => S.Editor.ConvertTabsToSpaces = v),
            new ToggleItem("Show whitespace", "", () => S.Editor.ShowWhitespace, v => S.Editor.ShowWhitespace = v),
            new ChoiceItem<EditorPlacement>("Open files in", "", () => S.Editor.OpenIn, v => S.Editor.OpenIn = v, O((EditorPlacement.Tab, "A tab"), (EditorPlacement.Window, "A separate window"))),
            new PathItem("External editor", "Used by \"Open with default app\" when set (e.g. Notepad++).", () => S.Editor.ExternalEditor, v => S.Editor.ExternalEditor = v, "Programs (*.exe)|*.exe")));

        // ============================================================ Backup & import
        pages.Add(new SettingPage { Key = "backup", Title = "Backup and import", Glyph = "", Group = "Backup", Description = "Move settings between PCs and bring in PuTTY configuration." }.Add(
            new HeaderItem("FluenTTY settings"),
            new ActionItem("Export settings", "Choose categories; saved passwords only with a passphrase.", "Export…", SettingsTransfer.ExportInteractive, accent: true, keywords: "backup save sync"),
            new ActionItem("Import settings", "From a file exported by FluenTTY.", "Import…", w => SettingsTransfer.ImportInteractive(w), keywords: "restore load sync"),
            new HeaderItem("PuTTY"),
            new ActionItem("Import PuTTY sessions", $"{PuttyImport.SessionNames().Count} saved session(s) found for your account.", "Import", w => ImportWindow.ImportPutty(w, sessions: true, defaults: false), keywords: "putty registry"),
            new ActionItem("Use PuTTY's default settings", "Font, colours, keyboard, bell, mouse buttons, proxy, keepalive and more from PuTTY's \"Default Settings\".", "Import",
                w => { if (FluentDialog.Confirm(w, "Use PuTTY's defaults?", "This replaces the matching FluenTTY settings with PuTTY's \"Default Settings\".", "Import")) ImportWindow.ImportPutty(w, sessions: false, defaults: true); }, keywords: "putty default settings"),
            new ActionItem("Import a PuTTY Connection Manager database", "A .dat database (or exported .xml) with folders of connections.", "Choose file…", w => ImportWindow.ImportPcm(w), keywords: "puttycm pcm dat connection manager"),
            new HeaderItem("Other"),
            new ActionItem("Welcome guide", "Show the first-run guide again.", "Open", w => { if (w?.Owner is Window o) OnboardingWindow.ShowFirstRun(o); else if (w != null) OnboardingWindow.ShowFirstRun(w); }),
            new ActionItem("Open the data folder", AppInfo.DataDirectory, "Open", _ => { try { Process.Start(new ProcessStartInfo(AppInfo.DataDirectory) { UseShellExecute = true }); } catch { } }),
            new ActionItem("Reset all settings", "Sessions and host keys are kept.", "Reset…", w =>
            {
                if (!FluentDialog.Confirm(w, "Reset all settings?", "Every setting goes back to its default. Saved sessions and host keys are kept.", "Reset", destructive: true)) return;
                var fresh = new AppSettings();
                fresh.Interface.OnboardingDone = true;
                SettingsStore.ReplaceSettings(fresh);
                if (w is SettingsWindow sw) sw.Reload();
            }, keywords: "defaults factory")));

        pages.Add(new SettingPage { Key = "config", Title = "Config file", Glyph = "", Group = "Backup", Description = "The raw settings.json, for power users (like Tabby's config file page)." }.Add(
            new CustomItem("settings.json", "Edits apply when you choose Save and apply.", ConfigFileEditor, keywords: "json raw advanced")));

        return pages;
    }

    private static FrameworkElement ConfigFileEditor()
    {
        var box = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, MinHeight = 380, MaxHeight = 520, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
            Text = System.Text.Json.JsonSerializer.Serialize(S, Json.Options),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "settings.json");
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        status.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        var save = new Button { Content = "Save and apply", Padding = new Thickness(14, 5, 14, 5) };
        save.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        save.Click += (_, _) =>
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(box.Text, Json.Options) ?? throw new FormatException("empty");
                SettingsStore.ReplaceSettings(parsed);
                status.Text = "Saved and applied.";
                if (Window.GetWindow(box) is SettingsWindow sw) sw.Reload(keepPage: true);
            }
            catch (Exception ex) { status.Text = "Invalid: " + ex.Message; }
        };
        var defaults = new Button { Content = "Show defaults", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        defaults.Click += (_, _) =>
        {
            var w = new Window { Title = "Default settings", Width = 700, Height = 600, Owner = Window.GetWindow(box), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            ThemeService.Attach(w);
            w.Content = new TextBox
            {
                Text = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), Json.Options), IsReadOnly = true, Margin = new Thickness(12),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            w.Show();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(save); buttons.Children.Add(defaults); buttons.Children.Add(status);
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        return panel;
    }
}
