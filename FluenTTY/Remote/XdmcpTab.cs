using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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
/// XDMCP session. XDMCP needs an X server on this PC; FluenTTY drives an installed one (VcXsrv, Xming
/// or Cygwin/X — MobaXterm bundles its own VcXsrv build for the same purpose) with -query / -broadcast
/// / -indirect, and embeds its window in the tab so it behaves like any other tab (pop-out and full
/// screen included).
/// </summary>
public sealed class XdmcpTab : SessionTab
{
    private readonly RemoteToolbar _toolbar = new();
    private readonly Grid _body = new();
    private readonly StackPanel _messagePanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560 };
    private Process? _server;
    private WindowsFormsHost? _host;
    private WinForms.Panel? _panel;
    private IntPtr _xWindow;
    private bool _shutdown;
    private readonly List<string> _log = new();

    public XdmcpTab(SessionProfile profile)
    {
        Profile = profile;
        Glyph = SessionProfile.ProtocolGlyph(ProtocolKind.Xdmcp);
        Title = string.IsNullOrEmpty(profile.Name) ? profile.DisplayTarget : profile.Name;

        _toolbar.Add("Full screen", "\uE740", "Full screen (F11)", () => RemoteToolbar.ToggleFullScreen(this));
        RemoteToolbar.HideWhenPoppedOut(this, _toolbar.Add("Pop out", "\uE8A7", "Move into its own window", () => RemoteToolbar.PopOut(this)));
        _toolbar.Add("Restart X server", "\uE72C", "Stop and start the X server session again", Reconnect);

        _body.Children.Add(_messagePanel);
        var dock = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        dock.Children.Add(_toolbar);
        dock.Children.Add(_body);
        Content = dock;

        _body.SizeChanged += (_, _) => FitWindow();
        bool started = false;
        Loaded += (_, _) => { if (!started) { started = true; Dispatcher.BeginInvoke(Start, DispatcherPriority.Loaded); } };
    }

    public override bool SupportsFullScreen => true;

    public override void SetOverlayOpen(bool open)
    {
        if (_host != null) _host.Visibility = open ? Visibility.Hidden : Visibility.Visible;
    }
    public override bool CanReconnect => true;
    public override bool IsLive => _server is { HasExited: false };

    private void Status(string s)
    {
        _log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{s}");
        _toolbar.Status = s;
    }

    /// <summary>A plain host name, IPv4 or IPv6 address: no spaces, quotes or leading '-' (no extra arguments).</summary>
    public static bool IsSafeHost(string host) =>
        !string.IsNullOrEmpty(host) && host.Length <= 253 && !host.StartsWith('-') &&
        System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9._:\[\]%-]+$");

    /// <summary>Settings path first, then the usual install locations.</summary>
    public static string? FindXServer()
    {
        var configured = SettingsStore.Settings.Xdmcp.XServerPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = Environment.ExpandEnvironmentVariables(configured.Trim('"'));
            if (File.Exists(p)) return p;
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var candidate in new[]
        {
            Path.Combine(pf, "VcXsrv", "vcxsrv.exe"), Path.Combine(pf86, "VcXsrv", "vcxsrv.exe"),
            Path.Combine(pf, "Xming", "Xming.exe"), Path.Combine(pf86, "Xming", "Xming.exe"),
            @"C:\cygwin64\bin\XWin.exe", @"C:\cygwin\bin\XWin.exe",
        })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static int FreeDisplay(int preferred)
    {
        if (preferred > 0) return preferred;
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        for (int d = 1; d < 64; d++) if (!listening.Contains(6000 + d)) return d;
        return 10;
    }

    private void ShowMessage(string title, string text, bool offerSettings)
    {
        _messagePanel.Children.Clear();
        var t = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        t.SetResourceReference(StyleProperty, "SectionTitle");
        var b = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        b.SetResourceReference(StyleProperty, "Body");
        _messagePanel.Children.Add(t);
        _messagePanel.Children.Add(b);
        if (offerSettings)
        {
            var btn = new Button { Content = "Open XDMCP settings", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 5, 14, 5) };
            btn.Click += (_, _) => SettingsWindow.ShowSingle(Window.GetWindow(this), "xdmcp");
            _messagePanel.Children.Add(btn);
        }
        _messagePanel.Visibility = Visibility.Visible;
    }

    private async void Start()
    {
        if (_shutdown) return;
        var exe = FindXServer();
        if (exe == null)
        {
            State = TabState.Disconnected;
            ShowMessage("An X server is needed for XDMCP",
                "Install VcXsrv (free and open source) or Xming, or set the path to your X server under Settings › XDMCP, then use Restart X server.",
                offerSettings: true);
            return;
        }
        var x = SettingsStore.Settings.Xdmcp;
        var p = Profile!;
        int display = FreeDisplay(x.DisplayNumber);
        var query = p.XdmcpQuery ?? x.QueryMode;
        // The host goes onto the X server's command line: a value like "10.0.0.1 -ac" from an imported
        // session would add options (here: turn off X access control). Security review M6.
        if (query != XdmcpQuery.Broadcast && !IsSafeHost(p.Host))
        {
            State = TabState.Disconnected;
            ShowMessage("Invalid host name", $"\"{p.Host}\" isn't a valid host name or address, so the X server wasn't started.", offerSettings: false);
            return;
        }
        var args = new List<string> { $":{display}" };
        switch (query)
        {
            case XdmcpQuery.Broadcast: args.Add("-broadcast"); break;
            case XdmcpQuery.Indirect: args.Add($"-indirect {p.Host}"); break;
            default: args.Add($"-query {p.Host}"); break;
        }
        if (p.Port > 0 && p.Port != 177) args.Add($"-port {p.Port}");
        bool vcx = Path.GetFileName(exe).Equals("vcxsrv.exe", StringComparison.OrdinalIgnoreCase);
        bool embed = x.WindowMode == XWindowMode.SingleWindow;
        if (x.WindowMode == XWindowMode.FullScreen) args.Add("-fullscreen");
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            string res = x.Resolution;
            if (string.IsNullOrWhiteSpace(res) || res.Equals("auto", StringComparison.OrdinalIgnoreCase))
                res = $"{(int)(Math.Max(640, _body.ActualWidth) * dpi.DpiScaleX)}x{(int)(Math.Max(480, _body.ActualHeight) * dpi.DpiScaleY)}";
            args.Add($"-screen 0 {res}");
        }
        args.Add("-once");
        if (x.ClipboardSync) args.Add("-clipboard");
        if (vcx) args.Add("-wgl");
        if (!string.IsNullOrWhiteSpace(x.ExtraArguments)) args.Add(x.ExtraArguments);

        var cmd = string.Join(" ", args);
        Status($"Starting {Path.GetFileName(exe)} {cmd}");
        State = TabState.Connecting;
        _messagePanel.Visibility = Visibility.Collapsed;
        try
        {
            _server = Process.Start(new ProcessStartInfo(exe, cmd) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            if (_server == null) throw new InvalidOperationException("The X server didn't start.");
            _server.EnableRaisingEvents = true;
            _server.Exited += (_, _) => Dispatcher.BeginInvoke(OnServerExited);
        }
        catch (Exception ex)
        {
            State = TabState.Disconnected;
            ShowMessage("Couldn't start the X server", ex.Message, offerSettings: true);
            return;
        }
        State = TabState.Connected;
        SettingsStore.NoteRecent(p);

        if (!embed)
        {
            ShowMessage("XDMCP session running", $"The X server ({Path.GetFileName(exe)}, display :{display}) is showing the session in its own window. Closing this tab stops it.", false);
            return;
        }
        // Wait for the X server's window, then adopt it into this tab.
        for (int i = 0; i < 50 && !_shutdown && _server is { HasExited: false }; i++)
        {
            await Task.Delay(200);
            _xWindow = FindServerWindow(_server.Id);
            if (_xWindow != IntPtr.Zero) break;
        }
        if (_xWindow == IntPtr.Zero)
        {
            ShowMessage("XDMCP session running", "The X server window couldn't be placed in the tab; it's showing in its own window.", false);
            return;
        }
        Embed();
    }

    private void Embed()
    {
        _panel = new WinForms.Panel { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
        _host = new WindowsFormsHost { Child = _panel };
        _body.Children.Insert(0, _host);
        Dispatcher.BeginInvoke(() =>
        {
            if (_panel == null || _xWindow == IntPtr.Zero) return;
            int style = GetWindowLong(_xWindow, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP);
            style |= WS_CHILD;
            SetWindowLong(_xWindow, GWL_STYLE, style);
            SetParent(_xWindow, _panel.Handle);
            FitWindow();
            ShowWindow(_xWindow, 5);
            Status("X server window embedded");
        }, DispatcherPriority.Loaded);
    }

    private void FitWindow()
    {
        if (_xWindow == IntPtr.Zero || _panel == null || !_panel.IsHandleCreated) return;
        MoveWindow(_xWindow, 0, 0, _panel.ClientSize.Width, _panel.ClientSize.Height, true);
    }

    private void OnServerExited()
    {
        if (_shutdown) return;
        Status("X server exited");
        State = TabState.Disconnected;
        RemoveHost();
        var rule = SettingsStore.Settings.Interface.CloseTabOnExit;
        if (rule != CloseOnExit.Never) { RequestClose(); return; }
        ShowMessage("XDMCP session ended", "The X server stopped (the remote session was logged out or the server was closed). Use Restart X server to start again.", false);
    }

    private void RemoveHost()
    {
        _xWindow = IntPtr.Zero;
        if (_host != null)
        {
            _body.Children.Remove(_host);
            _host.Dispose();
            _host = null;
        }
        _panel = null;
    }

    private void StopServer()
    {
        var s = _server;
        _server = null;
        if (s == null) return;
        try { if (!s.HasExited) s.Kill(entireProcessTree: true); } catch { }
        s.Dispose();
    }

    public override void Reconnect()
    {
        StopServer();
        RemoveHost();
        Dispatcher.BeginInvoke(Start, DispatcherPriority.Background);
    }

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Event log", "\uE81C", () => EventLogWindow.ShowFor(Window.GetWindow(this), Title, _log));
        yield return Item("Restart X server", "\uE72C", Reconnect);
    }

    public override void Shutdown()
    {
        _shutdown = true;
        if (SettingsStore.Settings.Xdmcp.StopServerWithTab) StopServer();
        else if (_xWindow != IntPtr.Zero) SetParent(_xWindow, IntPtr.Zero);
        RemoveHost();
    }

    // ------------------------------------------------------------------ Win32

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;

    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);

    /// <summary>The X server's visible top-level screen window (VcXsrv/Xming class "cygwin/x*").</summary>
    private static IntPtr FindServerWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != pid || !IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls.StartsWith("cygwin/x", StringComparison.OrdinalIgnoreCase) && !cls.Contains("tray", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
