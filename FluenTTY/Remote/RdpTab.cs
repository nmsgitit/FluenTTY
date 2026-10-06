using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Views;
using WinForms = System.Windows.Forms;

namespace FluenTTY.Remote;

/// <summary>
/// Remote Desktop session using the Remote Desktop ActiveX control that ships with Windows
/// (mstscax.dll, the same engine as mstsc.exe), hosted through WinForms. Settings › Remote Desktop maps
/// onto the control's AdvancedSettings / SecuredSettings / TransportSettings properties.
/// </summary>
public sealed class RdpTab : SessionTab
{
    private readonly RemoteToolbar _toolbar = new();
    private readonly Grid _body = new();
    private readonly TextBlock _message;
    private WindowsFormsHost? _host;
    private RdpAxHost? _ax;
    private AxHostEventCookie? _cookie;
    private readonly DispatcherTimer _resizeTimer;
    private bool _connected;
    private bool _shutdown;
    private readonly List<string> _log = new();

    public RdpTab(SessionProfile profile)
    {
        Profile = profile;
        Glyph = SessionProfile.ProtocolGlyph(ProtocolKind.Rdp);
        Title = string.IsNullOrEmpty(profile.Name) ? profile.DisplayTarget : profile.Name;

        _toolbar.Add("Ctrl+Alt+Del", "", "Ctrl+Alt+End is sent to the remote computer as Ctrl+Alt+Del", SendCtrlAltDel);
        _toolbar.Add("Full screen", "", "Full screen with the Remote Desktop connection bar", GoFullScreen);
        RemoteToolbar.HideWhenPoppedOut(this, _toolbar.Add("Pop out", "", "Move into its own window", () => RemoteToolbar.PopOut(this)));
        _toolbar.Add("Reconnect", "", "Reconnect", Reconnect);

        _message = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24) };
        _message.SetResourceReference(StyleProperty, "Body");
        _body.Children.Add(_message);

        var dock = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        dock.Children.Add(_toolbar);
        dock.Children.Add(_body);
        Content = dock;

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); UpdateResolution(); };
        _body.SizeChanged += (_, _) => { if (_connected) { _resizeTimer.Stop(); _resizeTimer.Start(); } };

        bool started = false;
        Loaded += (_, _) => { if (!started) { started = true; Dispatcher.BeginInvoke(Connect, DispatcherPriority.Loaded); } };
    }

    public override bool SupportsFullScreen => true;

    public override void SetOverlayOpen(bool open)
    {
        if (_host != null) _host.Visibility = open ? Visibility.Hidden : Visibility.Visible;
    }
    public override bool CanReconnect => true;

    private void Status(string s)
    {
        _log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{s}");
        _toolbar.Status = s;
        Diagnostics.Log($"[RDP {Title}] {s}");
    }

    private static string? _clsid;

    /// <summary>
    /// Newest Remote Desktop control that can actually be created on this PC. Registered isn't enough:
    /// on Windows 11 24H2 MsTscAx.MsTscAx.13 is registered but its class factory refuses
    /// (CLASS_E_CLASSNOTAVAILABLE), so each version is instantiated once to check.
    /// </summary>
    private static string? FindControlClsid()
    {
        if (_clsid != null) return _clsid;
        for (int v = 13; v >= 2; v--)
        {
            var t = Type.GetTypeFromProgID($"MsTscAx.MsTscAx.{v}");
            if (t == null) continue;
            try
            {
                var probe = Activator.CreateInstance(t);
                if (probe != null) Marshal.ReleaseComObject(probe);
                return _clsid = t.GUID.ToString("B");
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"RDP control MsTscAx.MsTscAx.{v} unavailable: {ex.Message}");
            }
        }
        return null;
    }

    private void Connect()
    {
        if (_shutdown || Profile == null) return;
        var clsid = FindControlClsid();
        if (clsid == null)
        {
            _message.Text = "The Windows Remote Desktop client component (mstscax.dll) isn't available on this PC.";
            State = TabState.Disconnected;
            return;
        }
        State = TabState.Connecting;
        _message.Visibility = Visibility.Collapsed;
        try
        {
            _ax = new RdpAxHost(clsid) { Dock = WinForms.DockStyle.Fill };
            var panel = new WinForms.Panel { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            panel.Controls.Add(_ax);
            _host = new WindowsFormsHost { Child = panel };
            _body.Children.Insert(0, _host);
            // The ActiveX control is created with its window, which needs the host to be in the visual tree first.
            Dispatcher.BeginInvoke(StartControl, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Status($"Couldn't start Remote Desktop: {ex.Message}");
            _message.Text = $"Couldn't start the Remote Desktop session:\n{ex.Message}";
            _message.Visibility = Visibility.Visible;
            State = TabState.Disconnected;
        }
    }

    private void StartControl()
    {
        if (_ax == null || _shutdown) return;
        try
        {
            _ax.CreateControl();
            var sink = new RdpEvents(this, Generation);
            _cookie = new AxHostEventCookie(_ax.Ocx!, sink);
            Configure(_ax.Ocx!);
            dynamic ocx = _ax.Ocx!;
            Status($"Connecting to {Profile!.HostWithPort}…");
            ocx.Connect();
        }
        catch (Exception ex)
        {
            Status($"Couldn't start Remote Desktop: {ex.Message}");
            _message.Text = $"Couldn't start the Remote Desktop session:\n{ex.Message}";
            _message.Visibility = Visibility.Visible;
            State = TabState.Disconnected;
        }
    }

    private (int w, int h) TargetSize()
    {
        var r = SettingsStore.Settings.Rdp;
        var mode = Profile!.RdpSizeMode ?? r.SizeMode;
        if (mode == RdpSizeMode.Fixed)
            return (Math.Clamp(Profile.RdpWidth ?? r.Width, 200, 8192), Math.Clamp(Profile.RdpHeight ?? r.Height, 200, 8192));
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Math.Max(200, _body.ActualWidth) * dpi.DpiScaleX);
        int h = (int)Math.Round(Math.Max(200, _body.ActualHeight) * dpi.DpiScaleY);
        return (Math.Clamp(w & ~1, 200, 8192), Math.Clamp(h, 200, 8192));
    }

    private void Configure(object ocxObj)
    {
        dynamic ocx = ocxObj;
        var r = SettingsStore.Settings.Rdp;
        var p = Profile!;
        ocx.Server = p.Host;
        if (!string.IsNullOrEmpty(p.Username)) ocx.UserName = p.Username;
        if (!string.IsNullOrEmpty(p.Domain)) ocx.Domain = p.Domain;
        var (w, h) = TargetSize();
        ocx.DesktopWidth = w;
        ocx.DesktopHeight = h;
        ocx.ColorDepth = r.ColorDepth;

        dynamic adv = Try(() => ocx.AdvancedSettings9) ?? Try(() => ocx.AdvancedSettings8) ?? Try(() => ocx.AdvancedSettings7) ?? ocx.AdvancedSettings2;
        Set(() => adv.RDPPort = p.EffectivePort);
        Set(() => adv.EnableCredSspSupport = r.NetworkLevelAuthentication);
        Set(() => adv.AuthenticationLevel = r.ServerAuthentication switch { RdpServerAuth.ConnectAnyway => 0u, RdpServerAuth.DoNotConnect => 1u, _ => 2u });
        Set(() => adv.RedirectClipboard = r.RedirectClipboard);
        Set(() => adv.RedirectDrives = r.RedirectDrives);
        Set(() => adv.RedirectPrinters = r.RedirectPrinters);
        Set(() => adv.RedirectSmartCards = r.RedirectSmartCards);
        Set(() => adv.RedirectPorts = r.RedirectPorts);
        Set(() => adv.SmartSizing = r.ResizeBehavior == RdpResizeBehavior.SmartSizing);
        Set(() => adv.ConnectToAdministerServer = p.RdpAdminSession ?? r.AdminSession);
        Set(() => adv.BitmapPersistence = r.PersistentBitmapCaching ? 1 : 0);
        Set(() => adv.Compress = r.Compression ? 1 : 0);
        Set(() => adv.EnableAutoReconnect = r.AutoReconnect);
        Set(() => adv.DisplayConnectionBar = r.ConnectionBarInFullScreen);
        Set(() => adv.AudioCaptureRedirectionMode = r.AudioCapture);
        int perf = 0;
        if (!r.DesktopBackground) perf |= 0x1;
        if (!r.ShowWindowContentsWhileDragging) perf |= 0x2;
        if (!r.MenuAnimations) perf |= 0x4;
        if (!r.VisualStyles) perf |= 0x8;
        if (r.FontSmoothing) perf |= 0x80;
        if (r.DesktopComposition) perf |= 0x100;
        Set(() => adv.PerformanceFlags = perf);
        if (r.ConnectionSpeed != RdpConnectionSpeed.AutoDetect)
            Set(() => adv.NetworkConnectionType = (uint)r.ConnectionSpeed);
        else Set(() => adv.NetworkConnectionType = 7u);

        dynamic? secured = Try(() => ocx.SecuredSettings3) ?? Try(() => ocx.SecuredSettings2);
        if (secured != null)
        {
            Set(() => secured.AudioRedirectionMode = r.Audio switch { RdpAudio.PlayOnRemoteComputer => 1, RdpAudio.DoNotPlay => 2, _ => 0 });
            Set(() => secured.KeyboardHookMode = r.KeyboardHook switch { RdpKeyboardHook.OnThisComputer => 0, RdpKeyboardHook.OnRemoteComputer => 1, _ => 2 });
        }

        if (r.GatewayMode != RdpGatewayMode.None && !string.IsNullOrWhiteSpace(r.GatewayHost))
        {
            dynamic? transport = Try(() => ocx.TransportSettings2);
            if (transport != null)
            {
                Set(() => transport.GatewayHostname = r.GatewayHost);
                Set(() => transport.GatewayUsageMethod = r.GatewayMode == RdpGatewayMode.Always ? 1u : 2u);
                Set(() => transport.GatewayProfileUsageMethod = 1u);
                Set(() => transport.GatewayCredsSource = 4u);
                Set(() => transport.GatewayCredSharing = r.GatewayUseSameCredentials ? 1u : 0u);
            }
        }

        var password = SecretStore.Unprotect(p.Password);
        if (!string.IsNullOrEmpty(password))
        {
            try { ((IMsTscNonScriptable)ocxObj).ClearTextPassword = password; }
            catch (Exception ex) { Status($"Couldn't pass the saved password to the control: {ex.Message}"); }
        }
    }

    private static object? Try(Func<object?> get)
    {
        try { return get(); } catch { return null; }
    }

    private void Set(Action action)
    {
        try { action(); }
        catch (Exception ex) { Diagnostics.Log($"RDP setting skipped: {ex.Message}"); }
    }

    private void UpdateResolution()
    {
        if (_ax?.Ocx == null || !_connected) return;
        var r = SettingsStore.Settings.Rdp;
        if ((Profile!.RdpSizeMode ?? r.SizeMode) != RdpSizeMode.FitToWindow || r.ResizeBehavior != RdpResizeBehavior.DynamicResolution) return;
        var (w, h) = TargetSize();
        var dpi = VisualTreeHelper.GetDpi(this);
        uint scale = (uint)Math.Clamp(Math.Round(dpi.DpiScaleX * 100 / 25) * 25, 100, 500);
        try
        {
            dynamic ocx = _ax.Ocx;
            ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, scale, 100u);
        }
        catch (Exception ex) { Diagnostics.Log($"RDP resize: {ex.Message}"); }
    }

    private void GoFullScreen()
    {
        if (_ax?.Ocx == null || !_connected) { RemoteToolbar.ToggleFullScreen(this); return; }
        try { dynamic ocx = _ax.Ocx; ocx.FullScreen = true; }
        catch { RemoteToolbar.ToggleFullScreen(this); }
    }

    private void SendCtrlAltDel()
    {
        // The control maps Ctrl+Alt+End to Ctrl+Alt+Del on the remote side; the Windows Security screen
        // itself can't be triggered from an app. Give the session focus and tell the user.
        _ax?.Focus();
        FluentDialog.Info(Window.GetWindow(this), "Ctrl+Alt+Del", "Click into the remote desktop and press Ctrl+Alt+End — Remote Desktop sends it to the remote computer as Ctrl+Alt+Del.");
    }

    // ================================================================== events from the control

    internal void OnConnecting() => Dispatcher.BeginInvoke(() => Status("Connecting…"));

    internal void OnConnected() => Dispatcher.BeginInvoke(() =>
    {
        _connected = true;
        State = TabState.Connected;
        Status($"Connected to {Profile!.HostWithPort}");
        SettingsStore.NoteRecent(Profile);
    });

    internal void OnLoginComplete() => Dispatcher.BeginInvoke(() => { Status("Signed in"); UpdateResolution(); });

    internal void OnDisconnected(int reason) => Dispatcher.BeginInvoke(() =>
    {
        _connected = false;
        string text = $"Disconnected (code {reason})";
        try
        {
            dynamic ocx = _ax!.Ocx!;
            int ext = (int)ocx.ExtendedDisconnectReason;
            string desc = ocx.GetErrorDescription((uint)reason, (uint)ext);
            if (!string.IsNullOrWhiteSpace(desc)) text = desc;
        }
        catch { }
        Status(text);
        State = TabState.Disconnected;
        // 1 = local disconnect, 2 = remote disconnect by user (logoff), 3 = remote disconnect by server
        bool clean = reason is 1 or 2 or 3;
        var rule = SettingsStore.Settings.Interface.CloseTabOnExit;
        if (!_shutdown && (rule == CloseOnExit.Always || (rule == CloseOnExit.OnCleanExit && clean && reason != 1)))
        {
            RequestClose();
            return;
        }
        _message.Text = text + "\nUse Reconnect to connect again.";
        _message.Visibility = Visibility.Visible;
        if (_host != null) _host.Visibility = Visibility.Collapsed;
    });

    internal void OnFatalError(int code) => Dispatcher.BeginInvoke(() => Status($"Remote Desktop control error {code}"));
    internal void OnWarning(int code) => Dispatcher.BeginInvoke(() => Status($"Warning {code}"));
    internal void OnLogonError(int code) => Dispatcher.BeginInvoke(() => Status($"Sign-in error {code}"));
    internal void OnRequestMinimize() => Dispatcher.BeginInvoke(() => { if (Window.GetWindow(this) is { } w) w.WindowState = WindowState.Minimized; });

    // ================================================================== tab lifecycle

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Full screen", "", GoFullScreen, _connected);
        yield return Item("Event log", "", () => EventLogWindow.ShowFor(Window.GetWindow(this), Title, _log));
        yield return Item("Reconnect", "", Reconnect);
    }

    public override void Reconnect()
    {
        TearDown();
        _message.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(Connect, DispatcherPriority.Background);
    }

    public override void OnActivated()
    {
        HasAlert = false;
        Dispatcher.BeginInvoke(() => _ax?.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Bumped whenever the control is torn down; its event sink ignores events from older controls.</summary>
    internal int Generation { get; private set; }

    private bool _reconnectAfterMove;

    /// <summary>
    /// The Remote Desktop ActiveX control can't be moved between top-level windows: re-parented, it keeps
    /// treating the old window as its frame, and focusing it re-activates that window — the pop-out and
    /// the main window then fight over activation (flicker, CPU, an unusable window). So the control is
    /// disconnected before the move and a new one connects in the new window. The remote session keeps
    /// running on the server, so this just reattaches to it.
    /// </summary>
    public override void BeforeMove()
    {
        _reconnectAfterMove = _ax != null && State is TabState.Connecting or TabState.Connected;
        if (_ax == null) return;
        TearDown();
        _message.Text = "Moving the session…";
        _message.Visibility = Visibility.Visible;
    }

    public override void AfterMove()
    {
        if (!_reconnectAfterMove) return;
        _reconnectAfterMove = false;
        Status("Reconnecting in the new window…");
        void Start() => Dispatcher.BeginInvoke(Connect, DispatcherPriority.Loaded);
        if (IsLoaded) Start();
        else
        {
            RoutedEventHandler? once = null;
            once = (_, _) => { Loaded -= once; Start(); };
            Loaded += once;
        }
    }

    private void TearDown()
    {
        Generation++;
        _connected = false;
        try
        {
            if (_ax?.Ocx is { } o)
            {
                dynamic ocx = o;
                if ((int)ocx.Connected != 0) ocx.Disconnect();
            }
        }
        catch { }
        _cookie?.Dispose();
        _cookie = null;
        if (_host != null)
        {
            _body.Children.Remove(_host);
            try { _host.Child?.Dispose(); } catch { }
            _host.Dispose();
            _host = null;
        }
        _ax = null;
    }

    public override void Shutdown()
    {
        _shutdown = true;
        TearDown();
    }
}

/// <summary>AxHost for the Remote Desktop control, created by CLSID (no aximp-generated wrapper needed).</summary>
internal sealed class RdpAxHost : WinForms.AxHost
{
    public RdpAxHost(string clsid) : base(clsid) { }
    public object? Ocx => GetOcx();
}

/// <summary>IMsTscNonScriptable (vtable order from mstscax.dll's type library): only the first member is used.</summary>
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    string ClearTextPassword { [param: MarshalAs(UnmanagedType.BStr)] set; }
}

/// <summary>IMsTscAxEvents dispinterface; DISPIDs as in mstscax.dll's type library.</summary>
[ComVisible(true), Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(22)] void OnLogonError(int lError);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEvents : IMsTscAxEvents
{
    private readonly RdpTab _tab;
    private readonly int _generation;
    /// <param name="generation">The tab's control generation; events from a control that was torn down
    /// (e.g. before moving to another window) are dropped instead of closing or updating the tab.</param>
    public RdpEvents(RdpTab tab, int generation) { _tab = tab; _generation = generation; }
    private bool Current => _tab.Generation == _generation;
    public void OnConnecting() { if (Current) _tab.OnConnecting(); }
    public void OnConnected() { if (Current) _tab.OnConnected(); }
    public void OnLoginComplete() { if (Current) _tab.OnLoginComplete(); }
    public void OnDisconnected(int discReason) { if (Current) _tab.OnDisconnected(discReason); }
    public void OnFatalError(int errorCode) { if (Current) _tab.OnFatalError(errorCode); }
    public void OnWarning(int warningCode) { if (Current) _tab.OnWarning(warningCode); }
    public void OnRequestContainerMinimize() { if (Current) _tab.OnRequestMinimize(); }
    public void OnLogonError(int lError) { if (Current) _tab.OnLogonError(lError); }
}

/// <summary>Connects an event sink to a COM object's connection point and disconnects on dispose.</summary>
internal sealed class AxHostEventCookie : IDisposable
{
    private System.Runtime.InteropServices.ComTypes.IConnectionPoint? _cp;
    private int _cookie;

    public AxHostEventCookie(object source, IMsTscAxEvents sink)
    {
        var container = (System.Runtime.InteropServices.ComTypes.IConnectionPointContainer)source;
        var iid = typeof(IMsTscAxEvents).GUID;
        container.FindConnectionPoint(ref iid, out _cp);
        _cp!.Advise(sink, out _cookie);
    }

    public void Dispose()
    {
        try { _cp?.Unadvise(_cookie); } catch { }
        _cp = null;
    }
}
