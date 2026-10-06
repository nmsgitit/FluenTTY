using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>
/// A tab moved into its own window (tab context menu › Pop out, or dragging a tab down out of the strip).
/// The session keeps running; "Return to tabs" puts it back. F11 toggles full screen.
/// </summary>
public sealed class PopOutWindow : Window, ITabHost
{
    private static readonly List<PopOutWindow> Open_ = new();
    public static IEnumerable<SessionTab> OpenTabs => Open_.Select(w => w._tab);

    private readonly SessionTab _tab;
    private readonly Grid _toolbar;
    private bool _returning;
    private bool _fullScreen;
    private WindowState _preState;
    private Rect _preBounds;

    public Window HostWindow => this;

    public static PopOutWindow Open(SessionTab tab)
    {
        var w = new PopOutWindow(tab);
        Open_.Add(w);
        w.Show();
        return w;
    }

    public static void CloseAll()
    {
        foreach (var w in Open_.ToList()) { w._returning = false; w.Close(); }
    }

    private PopOutWindow(SessionTab tab)
    {
        _tab = tab;
        Title = $"{tab.Title} — FluenTTY";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/FluenTTY.ico"));
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);
        UseLayoutRounding = true;

        var i = SettingsStore.Settings.Interface;
        Width = 900;
        Height = 600;
        if (tab is TerminalTab tt)
        {
            var size = tt.Canvas.SizeForGrid(i.PopOutColumns, i.PopOutRows);
            Width = size.Width + 30;
            Height = size.Height + 90;
        }
        if (Application.Current.MainWindow is { IsLoaded: true } main)
        {
            Left = main.Left + 60;
            Top = main.Top + 60;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        _toolbar = new Grid { Margin = new Thickness(6, 4, 6, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var back = new Button { Content = "Return to tabs", Tag = "", ToolTip = "Move this session back into the main window" };
        back.SetResourceReference(StyleProperty, "ToolbarButton");
        back.Click += (_, _) => ReturnToMain();
        var full = new Button { Content = "Full screen", Tag = "", ToolTip = "Full screen (F11; Shift+Esc to leave)" };
        full.SetResourceReference(StyleProperty, "ToolbarButton");
        full.Click += (_, _) => ToggleFullScreen();
        left.Children.Add(back);
        if (!tab.SupportsFullScreen) left.Children.Add(full); // remote tabs have their own Full screen button
        _toolbar.Children.Add(left);

        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_toolbar);
        var card = new Border { Child = tab, BorderThickness = new Thickness(0, 1, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        root.Children.Add(card);
        Content = root;

        tab.Host = this;
        tab.IsPoppedOut = true;
        tab.Visibility = Visibility.Visible;
        tab.Opacity = 1;
        tab.RenderTransform = null;
        tab.PropertyChanged += OnTabChanged;
        Activated += (_, _) => tab.OnActivated();
        Closing += OnClosingWindow;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => tab.OnActivated();
        SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (MainWindow.SuppressAltMenu(msg, w, l)) handled = true;
                return IntPtr.Zero;
            });
    }

    private void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTab.Title)) Dispatcher.BeginInvoke(() => Title = $"{_tab.Title} — FluenTTY");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.Shift && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
    }

    public void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _preState = WindowState;
            _preBounds = new Rect(Left, Top, Width, Height);
            _fullScreen = true;
            _toolbar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = screen.Left / dpi.DpiScaleX;
            Top = screen.Top / dpi.DpiScaleY;
            Width = screen.Width / dpi.DpiScaleX;
            Height = screen.Height / dpi.DpiScaleY;
            Topmost = true;
        }
        else
        {
            _fullScreen = false;
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            _toolbar.Visibility = Visibility.Visible;
            Left = _preBounds.Left; Top = _preBounds.Top; Width = _preBounds.Width; Height = _preBounds.Height;
            WindowState = _preState;
        }
        _tab.OnActivated();
    }

    public bool IsFullScreen => _fullScreen;

    private void ReturnToMain()
    {
        _returning = true;
        Close();
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_returning && Application.Current.MainWindow is MainWindow main && main.IsLoaded)
        {
            _tab.BeforeMove();
            Detach();
            main.Adopt(_tab);
            return;
        }
        if (_tab.IsLive && SettingsStore.Settings.Interface.WarnOnCloseActive && Application.Current.MainWindow?.IsVisible == true &&
            !FluentDialog.Confirm(this, "Close this session?", $"\"{_tab.Title}\" is still connected. Closing the window ends the session.", "Close"))
        {
            e.Cancel = true;
            return;
        }
        if (!_tab.ConfirmClose(this)) { e.Cancel = true; return; }
        Detach();
        _tab.Shutdown();
    }

    private void Detach()
    {
        _tab.PropertyChanged -= OnTabChanged;
        if (_tab.Parent is Border b) b.Child = null;
        _tab.Host = null;
        Open_.Remove(this);
    }

    // ITabHost
    public void CloseTab(SessionTab tab) => Close();
    public void ActivateTab(SessionTab tab) => Activate();
    public void Attention(SessionTab tab) { }
}
