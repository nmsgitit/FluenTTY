using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Terminal;
using FluenTTY.Views;

namespace FluenTTY.Remote;

/// <summary>A VNC session: framebuffer view with keyboard/mouse forwarding, scaling, clipboard sync.</summary>
public sealed class VncTab : SessionTab
{
    private readonly RemoteToolbar _toolbar = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly Border _surface;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _message;
    private VncClient? _client;
    private WriteableBitmap? _bitmap;
    private readonly DialogConnectionUi _ui;
    private CancellationTokenSource? _cts;
    private int _dirtyPending;
    private bool _scaled;
    private string? _lastClipboard;
    private readonly Dictionary<Key, uint> _downKeys = new();

    public VncTab(SessionProfile profile)
    {
        Profile = profile;
        Glyph = SessionProfile.ProtocolGlyph(ProtocolKind.Vnc);
        Title = string.IsNullOrEmpty(profile.Name) ? profile.DisplayTarget : profile.Name;
        _ui = new DialogConnectionUi(this, Title);
        _ui.StatusChanged += s => _toolbar.Status = s;
        _scaled = SettingsStore.Settings.Vnc.Scaling == VncScaling.FitToWindow;

        _toolbar.Add("Ctrl+Alt+Del", "", "Send Ctrl+Alt+Del to the remote computer", SendCtrlAltDel);
        _toolbar.Add("Scale", "", "Switch between fit-to-window and actual size", () => { _scaled = !_scaled; ApplyScaling(); });
        _toolbar.Add("Full screen", "", "Full screen (F11)", () => RemoteToolbar.ToggleFullScreen(this));
        RemoteToolbar.HideWhenPoppedOut(this, _toolbar.Add("Pop out", "", "Move into its own window", () => RemoteToolbar.PopOut(this)));
        _toolbar.Add("Reconnect", "", "Reconnect", Reconnect);

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _surface = new Border { Child = _image, Focusable = true, Background = Brushes.Black, FocusVisualStyle = null };
        _surface.Cursor = SettingsStore.Settings.Vnc.ShowRemoteCursor ? Cursors.Cross : Cursors.Arrow;
        _scroller = new ScrollViewer { Content = _surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _message = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), TextAlignment = TextAlignment.Center };
        _message.SetResourceReference(StyleProperty, "Body");

        var body = new Grid();
        body.Children.Add(_scroller);
        body.Children.Add(_message);
        var dock = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        dock.Children.Add(_toolbar);
        dock.Children.Add(body);
        Content = dock;

        _surface.MouseDown += OnMouse;
        _surface.MouseUp += OnMouse;
        _surface.MouseMove += OnMouse;
        _surface.MouseWheel += OnWheel;
        _surface.PreviewKeyDown += OnKeyDown;
        _surface.PreviewKeyUp += OnKeyUp;
        _surface.PreviewTextInput += OnText;
        _surface.LostKeyboardFocus += (_, _) => ReleaseAllKeys();

        bool started = false;
        Loaded += (_, _) => { if (!started) { started = true; _ = ConnectAsync(); } };
        ApplyScaling();
    }

    public override bool SupportsFullScreen => true;
    public override bool CanReconnect => true;

    private bool ViewOnly => Profile!.VncViewOnly ?? SettingsStore.Settings.Vnc.ViewOnly;

    private async Task ConnectAsync()
    {
        State = TabState.Connecting;
        _message.Text = $"Connecting to {Profile!.HostWithPort}…";
        _message.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var client = new VncClient();
        _client = client;
        client.FrameUpdated += (_, _, _, _) => ScheduleRepaint();
        client.DesktopResized += () => Dispatcher.BeginInvoke(CreateBitmap);
        client.ClipboardReceived += text => Dispatcher.BeginInvoke(() =>
        {
            if (!SettingsStore.Settings.Vnc.ClipboardSharing) return;
            _lastClipboard = text;
            TerminalCanvas.TrySetClipboard(text);
        });
        client.Bell += () => Dispatcher.BeginInvoke(() => System.Media.SystemSounds.Asterisk.Play());
        client.Closed += reason => Dispatcher.BeginInvoke(() => OnClosed(client, reason));
        try
        {
            int port = Profile.Port > 0 ? Profile.Port : 5900;
            var settings = SettingsStore.Settings;
            string? saved = SecretStore.Unprotect(Profile.Password);
            await Task.Run(() => client.ConnectAsync(Profile.Host, port, settings, Profile.UseProxy,
                () => saved ?? _ui.Prompt("VNC password: ", false), settings.Vnc, ViewOnly, _ui.Status, _cts.Token));
            if (!ReferenceEquals(client, _client)) return;
            State = TabState.Connected;
            _message.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(client.DesktopName) && string.IsNullOrEmpty(Profile.Name)) Title = client.DesktopName;
            _toolbar.Status = $"{client.DesktopName} · {client.Width}×{client.Height}{(ViewOnly ? " · view only" : "")}";
            SettingsStore.NoteRecent(Profile);
            CreateBitmap();
            _surface.Focus();
        }
        catch (Exception ex)
        {
            OnClosed(client, ex is OperationCanceledException ? "Cancelled" : ex.Message);
        }
    }

    private void OnClosed(VncClient client, string? reason)
    {
        if (!ReferenceEquals(client, _client)) return;
        State = TabState.Disconnected;
        _message.Text = (reason == null ? "The VNC session ended." : $"Disconnected: {reason}") + "\nUse Reconnect to try again.";
        _message.Visibility = Visibility.Visible;
        _toolbar.Status = reason ?? "Disconnected";
    }

    private void CreateBitmap()
    {
        var c = _client;
        if (c == null || c.Width == 0) return;
        _bitmap = new WriteableBitmap(c.Width, c.Height, 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        ApplyScaling();
        Repaint();
    }

    private void ApplyScaling()
    {
        if (_scaled)
        {
            _image.Stretch = Stretch.Uniform;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _image.Width = double.NaN; _image.Height = double.NaN;
        }
        else
        {
            _image.Stretch = Stretch.None;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            // One remote pixel = one screen pixel.
            var dpi = VisualTreeHelper.GetDpi(this);
            if (_bitmap != null) { _image.Width = _bitmap.PixelWidth / dpi.DpiScaleX; _image.Height = _bitmap.PixelHeight / dpi.DpiScaleY; _image.Stretch = Stretch.Fill; }
        }
    }

    private void ScheduleRepaint()
    {
        if (Interlocked.Exchange(ref _dirtyPending, 1) == 1) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref _dirtyPending, 0); Repaint(); }, DispatcherPriority.Render);
    }

    private void Repaint()
    {
        var c = _client;
        var bmp = _bitmap;
        if (c == null || bmp == null) return;
        lock (c.FrameLock)
        {
            if (bmp.PixelWidth != c.Width || bmp.PixelHeight != c.Height) return;
            bmp.WritePixels(new Int32Rect(0, 0, c.Width, c.Height), c.Frame, c.Width * 4, 0);
        }
    }

    // ================================================================== input

    private (int x, int y) ToRemote(Point p)
    {
        var c = _client!;
        double w = _image.ActualWidth, h = _image.ActualHeight;
        if (w <= 0 || h <= 0) return (0, 0);
        var pt = _surface.TranslatePoint(p, _image);
        return ((int)(pt.X * c.Width / w), (int)(pt.Y * c.Height / h));
    }

    private int ButtonMask(MouseEventArgs e) =>
        (e.LeftButton == MouseButtonState.Pressed ? 1 : 0) | (e.MiddleButton == MouseButtonState.Pressed ? 2 : 0) | (e.RightButton == MouseButtonState.Pressed ? 4 : 0);

    private void OnMouse(object sender, MouseEventArgs e)
    {
        if (e is MouseButtonEventArgs) _surface.Focus();
        if (_client == null || State != TabState.Connected || ViewOnly) return;
        var (x, y) = ToRemote(e.GetPosition(_surface));
        _client.SendPointer(ButtonMask(e), x, y);
        if (e is MouseButtonEventArgs mb) { if (mb.ButtonState == MouseButtonState.Pressed) _surface.CaptureMouse(); else if (ButtonMask(e) == 0) _surface.ReleaseMouseCapture(); }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_client == null || State != TabState.Connected || ViewOnly) return;
        var (x, y) = ToRemote(e.GetPosition(_surface));
        int bit = e.Delta > 0 ? 8 : 16;
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        for (int i = 0; i < notches; i++)
        {
            _client.SendPointer(ButtonMask(e) | bit, x, y);
            _client.SendPointer(ButtonMask(e), x, y);
        }
        e.Handled = true;
    }

    private static uint? SpecialKeysym(Key k) => k switch
    {
        Key.Back => 0xff08, Key.Tab => 0xff09, Key.Enter => 0xff0d, Key.Escape => 0xff1b, Key.Insert => 0xff63, Key.Delete => 0xffff,
        Key.Home => 0xff50, Key.End => 0xff57, Key.PageUp => 0xff55, Key.PageDown => 0xff56,
        Key.Left => 0xff51, Key.Up => 0xff52, Key.Right => 0xff53, Key.Down => 0xff54,
        Key.LeftShift => 0xffe1, Key.RightShift => 0xffe2, Key.LeftCtrl => 0xffe3, Key.RightCtrl => 0xffe4,
        Key.LeftAlt => 0xffe9, Key.RightAlt => 0xffea, Key.LWin => 0xffeb, Key.RWin => 0xffec, Key.Apps => 0xff67,
        Key.CapsLock => 0xffe5, Key.NumLock => 0xff7f, Key.Scroll => 0xff14, Key.Pause => 0xff13, Key.PrintScreen => 0xff61,
        >= Key.F1 and <= Key.F24 => (uint)(0xffbe + (k - Key.F1)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0xffb0 + (k - Key.NumPad0)),
        Key.Multiply => 0xffaa, Key.Add => 0xffab, Key.Subtract => 0xffad, Key.Decimal => 0xffae, Key.Divide => 0xffaf,
        _ => null,
    };

    private static uint CharKeysym(string s)
    {
        int cp = char.ConvertToUtf32(s, 0);
        return cp is >= 0x20 and <= 0xFF ? (uint)cp : 0x01000000u | (uint)cp;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_client == null || State != TabState.Connected) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11) return; // full screen toggle stays local
        if (ViewOnly) { e.Handled = true; return; }
        uint? sym = SpecialKeysym(key);
        var mods = Keyboard.Modifiers;
        if (sym == null && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            // Ctrl/Alt + letter doesn't produce text input; send the key's base character.
            var text = KeyEncoder.KeyText(key, (mods & ModifierKeys.Shift) != 0);
            if (!string.IsNullOrEmpty(text)) sym = CharKeysym(text);
        }
        if (sym == null) return; // plain characters arrive through TextInput
        // The local clipboard goes to the server only when you paste into the remote desktop (Ctrl+V /
        // Shift+Insert) or use "Send clipboard" — never just because the tab was activated.
        // Security review M7 — see CLAUDE.md "Security invariants".
        if ((key == Key.V && mods == ModifierKeys.Control) || (key == Key.Insert && mods == ModifierKeys.Shift)) SendClipboard();
        if (!_downKeys.ContainsKey(key)) _downKeys[key] = sym.Value;
        _client.SendKey(sym.Value, true);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_client == null || ViewOnly) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_downKeys.Remove(key, out var sym)) { _client.SendKey(sym, false); e.Handled = true; }
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (_client == null || State != TabState.Connected || ViewOnly || string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text)
        {
            if (ch < ' ') continue;
            var sym = CharKeysym(ch.ToString());
            _client.SendKey(sym, true);
            _client.SendKey(sym, false);
        }
        e.Handled = true;
    }

    private void ReleaseAllKeys()
    {
        if (_client == null) return;
        foreach (var sym in _downKeys.Values) _client.SendKey(sym, false);
        _downKeys.Clear();
    }

    private void SendCtrlAltDel()
    {
        if (_client == null || State != TabState.Connected || ViewOnly) return;
        _client.SendKey(0xffe3, true); _client.SendKey(0xffe9, true); _client.SendKey(0xffff, true);
        _client.SendKey(0xffff, false); _client.SendKey(0xffe9, false); _client.SendKey(0xffe3, false);
    }

    public override void OnActivated()
    {
        HasAlert = false;
        Dispatcher.BeginInvoke(() => _surface.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Hands the local clipboard text to the server (VNC is unencrypted: only on an explicit paste).</summary>
    private void SendClipboard()
    {
        if (_client == null || State != TabState.Connected || !SettingsStore.Settings.Vnc.ClipboardSharing || ViewOnly) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (text != _lastClipboard) { _lastClipboard = text; _client.SendClipboard(text); }
        }
        catch { }
    }

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Send Ctrl+Alt+Del", "", SendCtrlAltDel, State == TabState.Connected);
        yield return Item("Send clipboard to the remote computer", "", SendClipboard, State == TabState.Connected && SettingsStore.Settings.Vnc.ClipboardSharing && !ViewOnly);
        yield return Item("Event log", "", () => EventLogWindow.ShowFor(Window.GetWindow(this), Title, _ui.Log));
        yield return Item("Reconnect", "", Reconnect);
    }

    public override void Reconnect()
    {
        var old = _client;
        _client = null;
        _cts?.Cancel();
        Task.Run(() => old?.Dispose());
        _ = ConnectAsync();
    }

    public override void Shutdown()
    {
        _cts?.Cancel();
        var c = _client;
        _client = null;
        Task.Run(() => c?.Dispose());
    }
}
