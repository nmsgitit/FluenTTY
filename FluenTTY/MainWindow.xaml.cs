using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FluenTTY.Files;
using FluenTTY.Models;
using FluenTTY.Remote;
using FluenTTY.Services;
using FluenTTY.Views;

namespace FluenTTY;

/// <summary>
/// The tabbed main window. Tabs live in the title bar (custom WindowChrome caption with Fluent caption
/// buttons and Windows 11 snap layouts on Maximize); a sliding selection plate joins the selected
/// header to the content card, and tab content fades/slides in on switch — the same animation
/// approach as FluentPlay's pivot, disabled when Settings › Interface › Animations is off.
/// </summary>
public partial class MainWindow : Window, ITabHost, IDwmCaptionWindow, IUpdateHost
{
    private readonly List<SessionTab> _tabs = new();
    private readonly Dictionary<SessionTab, TabHeader> _headers = new();
    private SessionTab? _selected;
    private readonly string[] _args;
    private bool _closingConfirmed;
    private readonly UpdateBar _updateBar;
    private Action? _pendingUpdate;

    public Window HostWindow => this;

    public MainWindow(string[] args)
    {
        _args = args;
        InitializeComponent();
        _updateBar = new UpdateBar(this) { Margin = new Thickness(8, 0, 8, 8) };
        UpdateBarHost.Content = _updateBar;

        TabService.OpenTabHandler = (tab, activate) => OpenTab(tab, activate);
        TabService.OpenSessionHandler = p => OpenSession(p);

        Sidebar.ConnectRequested += (p, popOut) => OpenSession(p, popOut);
        Palette.Chosen += p => { if (p != null) OpenSession(p); };
        Palette.NewSessionRequested += text => EditNewSession(text);
        Palette.ManageRequested += () => { ShowSidebar(true); Sidebar.FocusSearch(); };

        RestorePlacement();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximizeState();
        TabArea.SizeChanged += (_, _) => LayoutTabs(animate: false);
        TabHost.SizeChanged += (_, _) => UpdateContentClip();
        TabScroller.PreviewMouseWheel += TabScroller_PreviewMouseWheel;
        SettingsStore.SettingsChanged += OnSettingsChanged;
        ThemeService.ThemeChanged += () => { foreach (var t in _tabs) t.ApplySettings(); };
        Activated += (_, _) => _selected?.OnActivated();
        ApplyInterfaceSettings();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ThemeService.ApplyTo(this);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
        UpdateMaximizeState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fromArgs = CommandLine.Parse(_args, out var error);
        if (fromArgs.Count > 0)
        {
            foreach (var p in fromArgs) OpenSession(p);
        }
        else if (SettingsStore.Settings.Interface.Startup == StartupAction.RestoreLastSessions && SettingsStore.Sessions.LastOpen.Count > 0)
        {
            foreach (var p in SettingsStore.Sessions.LastOpen.ToList()) OpenSession(p.Clone());
        }
        if (_tabs.Count == 0) OpenTab(new StartTab(), true);
        if (error != null) FluentDialog.Info(this, "Command line", error);
        SessionTab? underPalette = null; // the tab hidden behind the palette; choosing a row may switch tabs first
        Palette.IsVisibleChanged += (_, _) =>
        {
            underPalette?.SetOverlayOpen(false);
            underPalette = Palette.IsOpen ? _selected : null;
            underPalette?.SetOverlayOpen(true);
            if (!Palette.IsOpen) _selected?.OnActivated();
        };
        if (!SettingsStore.Settings.Interface.OnboardingDone && fromArgs.Count == 0)
            Dispatcher.BeginInvoke(() => OnboardingWindow.ShowFirstRun(this), DispatcherPriority.ApplicationIdle);
        if (AppInfo.IsPortable && AppInfo.DataDirectoryIsOnNetwork && !AppInfo.NetworkDataAcknowledged)
            Dispatcher.BeginInvoke(WarnSharedDataFolder, DispatcherPriority.ApplicationIdle);
        Dispatcher.BeginInvoke(StartUpdates, DispatcherPriority.ApplicationIdle);
    }

    // ================================================================== updates (Services\Updater, Views\UpdateUi)

    private void StartUpdates()
    {
        Updater.CleanUpDownloads();
        var news = Updater.TakeWhatsNew();
        if (news != null) WhatsNew.Show(this, news, afterUpdate: true);
        if (SettingsStore.Settings.Interface.CheckForUpdates) _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3)); // let sessions start first
        try
        {
            var release = await Updater.CheckAsync(manual: false, CancellationToken.None);
            if (release != null) ShowUpdate(release);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"update: check failed: {ex.Message}"); // offline etc.: try again next start
        }
    }

    public void ShowUpdate(Updater.ReleaseInfo release) => _updateBar.ShowAvailable(release);

    public int LiveSessionCount => _tabs.Concat(PopOutWindow.OpenTabs).Count(t => t.IsLive);

    public void CloseForUpdate(Action apply)
    {
        _pendingUpdate = apply;
        Close();
        _pendingUpdate = null; // still open: something (an unsaved file) said no
    }

    /// <summary>A second launch (or a shortcut) passed us a command line.</summary>
    public void OpenFromCommandLine(string[] args, bool fromOtherInstance)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var list = CommandLine.Parse(args, out var error);
        foreach (var p in list) OpenSession(p);
        if (list.Count == 0 && fromOtherInstance) OpenTab(new StartTab(), true);
        if (error != null) FluentDialog.Info(this, "Command line", error);
    }

    // ================================================================== opening sessions

    public void OpenSession(SessionProfile profile, bool popOut = false)
    {
        profile = profile.Clone();
        SessionTab tab = profile.Protocol switch
        {
            ProtocolKind.Sftp or ProtocolKind.Ftp => new FileBrowserTab(profile),
            ProtocolKind.Rdp => new RdpTab(profile),
            ProtocolKind.Vnc => new VncTab(profile),
            ProtocolKind.Xdmcp => new XdmcpTab(profile),
            _ => new TerminalTab(profile),
        };
        if (popOut)
        {
            PopOutWindow.Open(tab);
            return;
        }
        // Connecting from an empty start tab replaces it, like a browser's new-tab page.
        if (_selected is StartTab start && _tabs.Count > 0)
        {
            int index = _tabs.IndexOf(start);
            OpenTab(tab, true, index + 1);
            RemoveTab(start, animate: false);
            return;
        }
        OpenTab(tab, true);
    }

    public void EditNewSession(string? text = null)
    {
        var profile = string.IsNullOrWhiteSpace(text) ? new SessionProfile() : QuickConnect.Parse(text, ProtocolKind.Ssh) ?? new SessionProfile();
        var result = SessionEditorWindow.Edit(this, profile, isNew: true);
        if (result.Profile == null) return;
        if (result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
        if (result.Connect) OpenSession(result.Profile);
    }

    // ================================================================== ITabHost

    public void OpenTab(SessionTab tab, bool activate) => OpenTab(tab, activate, -1);

    private void OpenTab(SessionTab tab, bool activate, int index)
    {
        if (_tabs.Contains(tab)) { if (activate) ActivateTab(tab); return; }
        tab.Host = this;
        tab.IsPoppedOut = false;
        if (index < 0)
        {
            index = SettingsStore.Settings.Interface.NewTabPosition == NewTabPosition.AfterCurrent && _selected != null
                ? _tabs.IndexOf(_selected) + 1
                : _tabs.Count;
        }
        index = Math.Clamp(index, 0, _tabs.Count);
        _tabs.Insert(index, tab);

        var header = new TabHeader(tab);
        header.CloseClicked += h => CloseTab(h.Tab);
        header.PreviewMouseLeftButtonDown += Header_MouseDown;
        header.PreviewMouseMove += Header_MouseMove;
        header.PreviewMouseLeftButtonUp += Header_MouseUp;
        header.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(tab); e.Handled = true; } };
        header.MouseRightButtonUp += (_, e) => { ShowTabMenu(tab, header); e.Handled = true; };
        _headers[tab] = header;
        TabStrip.Children.Insert(index, header);

        tab.Visibility = Visibility.Collapsed;
        tab.RenderTransform = new TranslateTransform();
        TabHost.Children.Add(tab);

        LayoutTabs(animate: false);
        if (ThemeService.AnimationsEnabled)
        {
            // Grow in from nothing (width is animated only on the new header; it's small).
            double target = header.Width;
            header.Opacity = 0;
            header.BeginAnimation(WidthProperty, new DoubleAnimation(0, target, ThemeService.Anim(220)) { EasingFunction = ThemeService.EaseOut });
            header.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ThemeService.Anim(220)));
            DispatcherTimerOnce(240, () => { header.BeginAnimation(WidthProperty, null); LayoutTabs(false); MovePlate(true); });
        }
        if (activate || _selected == null) ActivateTab(tab);
        else MovePlate(false);
    }

    private void DispatcherTimerOnce(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    public void ActivateTab(SessionTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var previous = _selected;
        if (ReferenceEquals(previous, tab)) { tab.OnActivated(); return; }
        int oldIndex = previous == null ? -1 : _tabs.IndexOf(previous);
        int newIndex = _tabs.IndexOf(tab);
        _selected = tab;

        foreach (var (t, h) in _headers) h.IsSelected = ReferenceEquals(t, tab);
        foreach (var t in _tabs) if (!ReferenceEquals(t, tab)) { t.Visibility = Visibility.Collapsed; t.OnDeactivated(); }
        tab.Visibility = Visibility.Visible;
        tab.HasAlert = false;
        UpdateWindowTitle();
        MovePlate(previous != null);
        BringSelectedIntoView();
        PlayEntrance(tab, fromLeft: oldIndex > newIndex);
        tab.OnActivated();
    }

    public void CloseTab(SessionTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        if (tab.IsLive && SettingsStore.Settings.Interface.WarnOnCloseActive &&
            !FluentDialog.Confirm(this, "Close this session?", $"\"{tab.Title}\" is still connected. Closing the tab ends the session.", "Close", "Cancel"))
            return;
        if (!tab.ConfirmClose(this)) return;
        tab.Shutdown();
        RemoveTab(tab, animate: true);
        if (_tabs.Count == 0) OpenTab(new StartTab(), true);
    }

    /// <summary>Takes a tab out of the strip without ending its session (pop-out, tear-off, replace).</summary>
    private void RemoveTab(SessionTab tab, bool animate)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        var header = _headers[tab];
        _headers.Remove(tab);
        header.Detach();
        TabHost.Children.Remove(tab);
        tab.Host = null;

        if (ReferenceEquals(_selected, tab))
        {
            _selected = null;
            if (_tabs.Count > 0) ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        if (animate && ThemeService.AnimationsEnabled)
        {
            header.IsHitTestVisible = false;
            var shrink = new DoubleAnimation(header.ActualWidth, 0, ThemeService.Anim(180)) { EasingFunction = ThemeService.EaseOut };
            shrink.Completed += (_, _) => { TabStrip.Children.Remove(header); LayoutTabs(false); MovePlate(true); };
            header.BeginAnimation(OpacityProperty, new DoubleAnimation(0, ThemeService.Anim(140)));
            header.BeginAnimation(WidthProperty, shrink);
        }
        else
        {
            TabStrip.Children.Remove(header);
            LayoutTabs(false);
            MovePlate(false);
        }
    }

    public void Attention(SessionTab tab)
    {
        if (_headers.TryGetValue(tab, out var h)) h.Refresh();
    }

    /// <summary>Moves a tab into its own window; the session keeps running.</summary>
    public void PopOut(SessionTab tab)
    {
        if (!tab.CanPopOut) return;
        tab.BeforeMove();
        RemoveTab(tab, animate: true);
        PopOutWindow.Open(tab);
        tab.AfterMove();
        if (_tabs.Count == 0) OpenTab(new StartTab(), true);
    }

    /// <summary>A pop-out window hands its tab back to the strip (it already called BeforeMove).</summary>
    public void Adopt(SessionTab tab)
    {
        if (_selected is StartTab start && _tabs.Count == 1)
        {
            OpenTab(tab, true);
            RemoveTab(start, animate: false);
        }
        else OpenTab(tab, true);
        Activate();
        tab.AfterMove();
    }

    // ================================================================== layout + animation

    private void LayoutTabs(bool animate)
    {
        if (_tabs.Count == 0) return;
        // Room left of the new-tab / connect buttons, which always stay visible.
        double available = Math.Max(100, TabArea.ActualWidth - 80);
        TabScroller.MaxWidth = available;
        double max = Math.Clamp(SettingsStore.Settings.Interface.MaxTabWidth, 120, 400);
        double width = Math.Clamp(Math.Floor(available / _tabs.Count), TabHeader.MinWidthForClose, max);
        foreach (var header in _headers.Values)
        {
            if (header.HasAnimatedProperties && header.IsHitTestVisible == false) continue;
            header.Width = width;
        }
        TabStrip.UpdateLayout();
        MovePlate(animate);
        BringSelectedIntoView();
    }

    private void BringSelectedIntoView()
    {
        if (_selected != null && _headers.TryGetValue(_selected, out var header))
            Dispatcher.BeginInvoke(() => header.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0) return;
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void MovePlate(bool animate)
    {
        if (_selected == null || !_headers.TryGetValue(_selected, out var header))
        {
            SelectionPlate.Opacity = 0;
            return;
        }
        TabStrip.UpdateLayout();
        double x = header.TranslatePoint(new Point(0, 0), TabLayer).X + TabHeader.PlateInset;
        double w = Math.Max(1, (header.Width > 0 ? header.Width : header.ActualWidth) - 2 * TabHeader.PlateInset);
        SelectionPlate.Opacity = 1;
        if (!animate || !ThemeService.AnimationsEnabled || SelectionPlate.Width <= 1)
        {
            PlateOffset.BeginAnimation(TranslateTransform.XProperty, null);
            SelectionPlate.BeginAnimation(WidthProperty, null);
            PlateOffset.X = x;
            SelectionPlate.Width = w;
            return;
        }
        var d = ThemeService.Anim(250);
        PlateOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, d) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
        SelectionPlate.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private int _entranceGeneration;

    private void PlayEntrance(SessionTab tab, bool fromLeft)
    {
        if (!ThemeService.AnimationsEnabled || tab.RenderTransform is not TranslateTransform slide || tab is RdpTab || tab is XdmcpTab)
        {
            tab.Opacity = 1;
            return;
        }
        int generation = ++_entranceGeneration;
        tab.CacheMode = new BitmapCache { SnapsToDevicePixels = false };
        var duration = ThemeService.Anim(260);
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ThemeService.EaseOut };
        fade.Completed += (_, _) => { if (generation == _entranceGeneration) tab.CacheMode = null; };
        tab.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromLeft ? -28 : 28, 0, duration) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    // ================================================================== tab dragging (reorder / tear-off)

    private Point _dragStart;
    private TabHeader? _dragHeader;
    private bool _dragging;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabHeader h) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; // close button
        ActivateTab(h.Tab);
        _dragHeader = h;
        _dragStart = e.GetPosition(TabStrip);
        _dragging = false;
        h.CaptureMouse();
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHeader == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(TabStrip);
        if (!_dragging && Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
        _dragging = true;

        // Pulled well below the tab strip: tear off into its own window.
        if (p.Y > TabStrip.ActualHeight + 60 && _dragHeader.Tab.CanPopOut)
        {
            var tab = _dragHeader.Tab;
            _dragHeader.ReleaseMouseCapture();
            _dragHeader = null;
            _dragging = false;
            PopOut(tab);
            return;
        }

        int from = _tabs.IndexOf(_dragHeader.Tab);
        double w = _dragHeader.ActualWidth;
        int to = Math.Clamp((int)(p.X / Math.Max(1, w)), 0, _tabs.Count - 1);
        if (to != from)
        {
            var tab = _tabs[from];
            _tabs.RemoveAt(from);
            _tabs.Insert(to, tab);
            TabStrip.Children.Remove(_dragHeader);
            TabStrip.Children.Insert(to, _dragHeader);
            _dragHeader.CaptureMouse();
            MovePlate(true);
        }
    }

    private void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragHeader?.ReleaseMouseCapture();
        _dragHeader = null;
        _dragging = false;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void ShowTabMenu(SessionTab tab, TabHeader header)
    {
        var menu = new ContextMenu();
        MenuItem Mi(string text, string glyph, Action a, bool enabled = true, string gesture = "")
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled, InputGestureText = gesture };
            mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 };
            mi.Click += (_, _) => a();
            return mi;
        }
        menu.Items.Add(Mi("Duplicate tab", "", () => { if (tab.Profile != null) OpenSession(tab.Profile); }, tab.CanDuplicate, "Ctrl+Shift+D"));
        menu.Items.Add(Mi("Rename tab…", "", () =>
        {
            var name = FluentDialog.PromptText(this, "Rename tab", "Tab title:", tab.Title);
            if (!string.IsNullOrWhiteSpace(name)) tab.Title = name;
        }));
        menu.Items.Add(Mi("Pop out into a window", "", () => PopOut(tab), tab.CanPopOut));
        if (tab.Profile != null && !SettingsStore.Sessions.Sessions.Any(s => s.Id == tab.Profile.Id))
            menu.Items.Add(Mi("Save as session…", "", () => SaveTabAsSession(tab)));
        var extra = tab.MenuItems().ToList();
        if (extra.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var item in extra) menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("Close", "", () => CloseTab(tab), true, "Ctrl+Shift+W"));
        menu.Items.Add(Mi("Close other tabs", "", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); }, _tabs.Count > 1));
        menu.Items.Add(Mi("Close tabs to the right", "", () =>
        {
            int i = _tabs.IndexOf(tab);
            foreach (var t in _tabs.Skip(i + 1).ToList()) CloseTab(t);
        }, _tabs.IndexOf(tab) < _tabs.Count - 1));
        menu.PlacementTarget = header;
        menu.IsOpen = true;
    }

    private void SaveTabAsSession(SessionTab tab)
    {
        var copy = tab.Profile!.Clone();
        copy.Id = Guid.NewGuid();
        if (string.IsNullOrEmpty(copy.Name)) copy.Name = copy.DisplayTarget;
        var result = SessionEditorWindow.Edit(this, copy, isNew: true);
        if (result.Profile != null && result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
    }

    // ================================================================== title bar buttons

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTab(new StartTab(), true);

    private void Connect_Click(object sender, RoutedEventArgs e) => Palette.Open();

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.ShowSingle(this);

    private void About_Click(object sender, RoutedEventArgs e) => AboutWindow.ShowSingle(this);

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ShowSidebar(SidebarColumn.ActualWidth < 10);

    private void ShowSidebar(bool show)
    {
        SettingsStore.Settings.Interface.ShowSessionSidebar = show;
        ApplySidebar(animate: true);
        SettingsStore.NotifySettingsChanged();
    }

    private void ApplySidebar(bool animate)
    {
        bool show = SettingsStore.Settings.Interface.ShowSessionSidebar && !_fullScreen;
        double width = show ? Math.Clamp(SettingsStore.Settings.Interface.SidebarWidth, 180, 600) : 0;
        SidebarCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = SidebarCard.Visibility;
        SidebarColumn.Width = new GridLength(width);
        // Rounded cards with a gutter around them; full screen goes edge to edge.
        Body.Margin = new Thickness(0, _fullScreen ? 0 : CardGap / 2, 0, 0);
        ContentCard.Margin = _fullScreen ? new Thickness(0) : new Thickness(show ? 0 : CardGap, 0, CardGap, CardGap);
        ContentCard.CornerRadius = new CornerRadius(_fullScreen ? 0 : CardRadius);
        ContentCard.BorderThickness = new Thickness(_fullScreen ? 0 : 1);
        UpdateContentClip();
        if (show && animate && ThemeService.AnimationsEnabled)
        {
            SidebarCard.RenderTransform = new TranslateTransform(-24, 0);
            SidebarCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ThemeService.Anim(220)));
            ((TranslateTransform)SidebarCard.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-24, 0, ThemeService.Anim(220)) { EasingFunction = ThemeService.EaseOut });
        }
    }

    private const double CardGap = 8, CardRadius = 8;

    /// <summary>
    /// Clips the tabs to the content card's rounded inner edge (terminals paint their own square background).
    /// Native windows (RDP, X server) ignore WPF clipping, so their corners stay square.
    /// </summary>
    private void UpdateContentClip()
    {
        double r = Math.Max(0, ContentCard.CornerRadius.TopLeft - ContentCard.BorderThickness.Left);
        TabHost.Clip = r <= 0 || TabHost.ActualWidth <= 0 ? null : new RectangleGeometry(new Rect(TabHost.RenderSize), r, r);
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SettingsStore.Settings.Interface.SidebarWidth = SidebarColumn.ActualWidth;
        if (SidebarColumn.ActualWidth < 60) ShowSidebar(false);
        else SettingsStore.NotifySettingsChanged();
    }

    private void UpdateMaximizeState()
    {
        bool max = WindowState == WindowState.Maximized;
        UpdateCaptionSpacer();
        // A maximized WindowChrome window extends past the screen by the resize border; pad it back in.
        Root.Margin = max && !_fullScreen ? new Thickness(7) : new Thickness(0);
    }

    // ================================================================== full screen

    private bool _fullScreen;
    private Rect _preFullScreen;
    private WindowState _preFullScreenState;

    public void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _preFullScreenState = WindowState;
            _preFullScreen = new Rect(Left, Top, Width, Height);
            _fullScreen = true;
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowState = WindowState.Normal;
            TitleRow.Height = new GridLength(0);
            TitleBar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = screen.Left / dpi.DpiScaleX;
            Top = screen.Top / dpi.DpiScaleY;
            Width = screen.Width / dpi.DpiScaleX;
            Height = screen.Height / dpi.DpiScaleY;
            Topmost = true;
        }
        else
        {
            _fullScreen = false;
            Topmost = SettingsStore.Settings.Interface.AlwaysOnTop;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            TitleRow.Height = new GridLength(40);
            TitleBar.Visibility = Visibility.Visible;
            Left = _preFullScreen.Left; Top = _preFullScreen.Top; Width = _preFullScreen.Width; Height = _preFullScreen.Height;
            WindowState = _preFullScreenState;
        }
        ApplySidebar(animate: false);
        UpdateMaximizeState();
        _selected?.OnActivated();
    }

    // ================================================================== keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var kb = SettingsStore.Settings.Keyboard;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        if (Palette.IsOpen) { base.OnPreviewKeyDown(e); return; }

        if (key == Key.F11 && mods == ModifierKeys.None) { ToggleFullScreen(); e.Handled = true; return; }
        if (key == Key.Enter && mods == ModifierKeys.Alt && SettingsStore.Settings.Interface.FullScreenOnAltEnter) { ToggleFullScreen(); e.Handled = true; return; }
        if (key == Key.Escape && _fullScreen && mods == ModifierKeys.Shift) { ToggleFullScreen(); e.Handled = true; return; }
        if (key == Key.OemComma && mods == ModifierKeys.Control) { SettingsWindow.ShowSingle(this); e.Handled = true; return; }

        if (kb.TabShortcuts)
        {
            if (ctrlShift)
            {
                switch (key)
                {
                    case Key.T: OpenTab(new StartTab(), true); e.Handled = true; return;
                    case Key.W: if (_selected != null) CloseTab(_selected); e.Handled = true; return;
                    case Key.P: Palette.Open(); e.Handled = true; return;
                    case Key.N: EditNewSession(); e.Handled = true; return;
                    case Key.D: if (_selected?.Profile != null && _selected.CanDuplicate) OpenSession(_selected.Profile); e.Handled = true; return;
                    case Key.B: ShowSidebar(SidebarColumn.ActualWidth < 10); e.Handled = true; return;
                }
            }
            if (key == Key.Tab && (mods & ModifierKeys.Control) != 0 && _tabs.Count > 1 && _selected != null)
            {
                int i = _tabs.IndexOf(_selected);
                i = (mods & ModifierKeys.Shift) != 0 ? (i - 1 + _tabs.Count) % _tabs.Count : (i + 1) % _tabs.Count;
                ActivateTab(_tabs[i]);
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D9)
            {
                int n = key - Key.D1;
                if (key == Key.D9) n = _tabs.Count - 1;
                if (n < _tabs.Count) ActivateTab(_tabs[n]);
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    // ================================================================== settings / theme

    private void OnSettingsChanged()
    {
        ApplyInterfaceSettings();
        foreach (var t in _tabs) t.ApplySettings();
        foreach (var h in _headers.Values) h.Refresh();
        LayoutTabs(false);
    }

    private void ApplyInterfaceSettings()
    {
        if (!_fullScreen) Topmost = SettingsStore.Settings.Interface.AlwaysOnTop;
        ApplySidebar(animate: false);
    }

    private void UpdateWindowTitle()
    {
        Title = _selected == null || _selected is StartTab ? "FluenTTY" : $"{_selected.Title} — FluenTTY";
    }

    // ================================================================== window chrome: native caption buttons

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;

    /// <summary>Reserves exactly the width of the Windows caption buttons (min/max/close) at the right of the title bar.</summary>
    /// <summary>Portable copy running with its settings on a network share (security review M6).</summary>
    private void WarnSharedDataFolder()
    {
        var panel = new StackPanel();
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"This portable copy keeps its settings, sessions and trusted host keys in\n{AppInfo.DataDirectory}\n\n" +
                   "That folder is on a network share. Anyone who can change files there can change what FluenTTY runs and which " +
                   "servers it trusts. Keep a personal copy on this PC, or make sure only you can write to that folder. " +
                   "(Saved passwords stay protected: they only work for your Windows account.)",
        };
        text.SetResourceReference(StyleProperty, "Body");
        var dontAsk = new CheckBox { Content = "Don't warn me about this folder again", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(text);
        panel.Children.Add(dontAsk);
        new FluentDialog(this, "Settings are on a shared folder", panel, "OK", null, null).ShowDialog();
        if (dontAsk.IsChecked == true) AppInfo.NetworkDataAcknowledged = true;
    }

    public void BackdropApplied(bool backdrop)
    {
        if (backdrop) BodyBackground.ClearValue(Border.BackgroundProperty);
        else BodyBackground.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
    }

    private void UpdateCaptionSpacer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            CaptionSpacer.Width = (r.Right - r.Left) / dpi.DpiScaleX + 8;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0, WM_SIZE = 0x0005;
        if (msg is WM_DPICHANGED or WM_SIZE) Dispatcher.BeginInvoke(UpdateCaptionSpacer, DispatcherPriority.Background);
        if (SuppressAltMenu(msg, wParam, lParam)) { handled = true; }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Like PuTTY: pressing and releasing Alt alone must not put the window into menu mode (which
    /// would swallow the next key typed into the terminal). Alt+Space still opens the window menu
    /// when Settings › Keyboard allows it.
    /// </summary>
    public static bool SuppressAltMenu(int msg, IntPtr wParam, IntPtr lParam)
    {
        const int WM_SYSCOMMAND = 0x0112, SC_KEYMENU = 0xF100;
        if (msg != WM_SYSCOMMAND || (wParam.ToInt64() & 0xFFF0) != SC_KEYMENU) return false;
        long ch = lParam.ToInt64();
        if (ch == ' ' && SettingsStore.Settings.Keyboard.AltSpaceSystemMenu) return false;
        return true;
    }
    // ================================================================== placement + closing

    private sealed class Placement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string PlacementPath => Path.Combine(AppInfo.DataDirectory, "window.json");

    private void RestorePlacement()
    {
        var p = Json.Load<Placement>(PlacementPath);
        if (p == null || p.Width < 300 || p.Height < 200) return;
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virt.IntersectsWith(new Rect(p.Left, p.Top, p.Width, p.Height))) return;
        // Keep the whole window on screen (resolution or scaling may have changed since it was saved).
        var work = SystemParameters.WorkArea;
        double w = Math.Min(p.Width, work.Width), h = Math.Min(p.Height, work.Height);
        double left = p.Left, top = p.Top;
        if (virt.Width <= work.Width + 1)
        {
            left = Math.Clamp(left, work.Left, work.Right - w);
            top = Math.Clamp(top, work.Top, work.Bottom - h);
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left; Top = top; Width = w; Height = h;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var live = _tabs.Concat(PopOutWindow.OpenTabs).Where(t => t.IsLive).ToList();
        // An update already told the user which sessions end.
        if (!_closingConfirmed && _pendingUpdate == null && live.Count > 0 && SettingsStore.Settings.Interface.WarnOnExitWithSessions)
        {
            if (!FluentDialog.Confirm(this, "Close FluenTTY?", $"{live.Count} session{(live.Count == 1 ? " is" : "s are")} still connected. Closing FluenTTY ends {(live.Count == 1 ? "it" : "them")}.", "Close all", "Cancel"))
            {
                e.Cancel = true;
                return;
            }
        }
        foreach (var t in _tabs.ToList())
        {
            if (!t.ConfirmClose(this)) { e.Cancel = true; return; }
        }
        SettingsStore.Sessions.LastOpen = _tabs.Where(t => t.Profile != null && t is not EditorTab).Select(t => t.Profile!.Clone()).ToList();
        if (_pendingUpdate != null)
        {
            // Saves and backs up the user's data, then hands over to the setup / new exe (which waits for
            // this process to exit). If that fails nothing has changed: stay open with sessions intact.
            try { _pendingUpdate(); }
            catch (Exception ex)
            {
                Diagnostics.Log($"update: apply failed: {ex}");
                e.Cancel = true;
                FluentDialog.Info(this, "Couldn't install the update",
                    $"FluenTTY is still on version {Updater.Current.ToString(3)} and nothing was changed.\n\n{ex.Message}\n\nYou can download the new version from the release page instead.");
                return;
            }
        }
        _closingConfirmed = true;

        if (_fullScreen) ToggleFullScreen();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        try { Json.Save(PlacementPath, new Placement { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Maximized = WindowState == WindowState.Maximized }); }
        catch { }

        foreach (var t in _tabs.ToList()) t.Shutdown();
        PopOutWindow.CloseAll();
        SettingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
