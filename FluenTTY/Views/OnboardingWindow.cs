using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>
/// First-run welcome: pick a theme, bring in connections from PuTTY (saved sessions and, optionally,
/// its default settings), a PuTTY Connection Manager database, or a FluenTTY settings export.
/// Can be reopened from Settings › Backup &amp; import.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly Grid _host = new() { ClipToBounds = true };
    private readonly List<FrameworkElement> _pages = new();
    private int _page;
    private readonly Button _back, _next, _skip;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private CheckBox? _puttySessions, _puttyDefaults;
    private TextBlock? _pcmResult;

    public static void ShowFirstRun(Window owner)
    {
        var w = new OnboardingWindow { Owner = owner };
        w.ShowDialog();
    }

    private OnboardingWindow()
    {
        Title = "Welcome to FluenTTY";
        Width = 720;
        Height = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);

        _pages.Add(WelcomePage());
        _pages.Add(ImportPage());
        _pages.Add(DonePage());
        foreach (var p in _pages)
        {
            p.Visibility = Visibility.Collapsed;
            p.RenderTransform = new TranslateTransform();
            _host.Children.Add(p);
        }

        _back = new Button { Content = "Back", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        _back.Click += (_, _) => Go(_page - 1);
        _next = new Button { Content = "Next", Padding = new Thickness(20, 6, 20, 6), IsDefault = true };
        _next.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _next.Click += (_, _) => NextClicked();
        _skip = new Button { Content = "Skip", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        _skip.Click += (_, _) => Go(_pages.Count - 1);

        var footer = new Grid { Margin = new Thickness(28, 0, 28, 22) };
        footer.Children.Add(_dots);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_host);
        Content = root;

        Closed += (_, _) =>
        {
            SettingsStore.Settings.Interface.OnboardingDone = true;
            SettingsStore.NotifySettingsChanged();
        };
        Go(0);
    }

    private void NextClicked()
    {
        if (_page == 1) RunPuttyImport();
        if (_page == _pages.Count - 1) { Close(); return; }
        Go(_page + 1);
    }

    private void Go(int index)
    {
        index = Math.Clamp(index, 0, _pages.Count - 1);
        bool forward = index >= _page;
        var old = _pages[_page];
        _page = index;
        var page = _pages[index];
        foreach (var p in _pages) if (p != page) p.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        if (old != page && ThemeService.AnimationsEnabled)
        {
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ThemeService.Anim(260)));
            ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(forward ? 40 : -40, 0, ThemeService.Anim(300)) { EasingFunction = ThemeService.EaseOut });
        }
        _back.Visibility = index == 0 || index == _pages.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = index == _pages.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = index switch { 0 => "Get started", 1 => "Import and continue", _ => "Start using FluenTTY" };

        _dots.Children.Clear();
        for (int i = 0; i < _pages.Count; i++)
        {
            var dot = new Border { Width = i == index ? 18 : 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, i == index ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDisabledBrush");
            _dots.Children.Add(dot);
        }
    }

    // ------------------------------------------------------------------ pages

    private static TextBlock Text(string text, string style, double top = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(StyleProperty, style);
        return t;
    }

    private FrameworkElement WelcomePage()
    {
        var sp = new StackPanel { Margin = new Thickness(40, 34, 40, 10) };
        sp.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/FluenTTY.png")), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });
        sp.Children.Add(Text("Welcome to FluenTTY", "PageTitle", 16));
        sp.Children.Add(Text("A tabbed client for SSH, serial consoles, Telnet, SFTP/FTP, Remote Desktop and VNC — with the PuTTY settings you know, in a Fluent window.", "Body", 8));
        sp.Children.Add(Text("Choose a look", "BodyStrong", 26));

        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (theme, label, glyph) in new[] { (AppTheme.System, "Windows default", ""), (AppTheme.Light, "Light", ""), (AppTheme.Dark, "Dark", "") })
        {
            var rb = new RadioButton { GroupName = "theme", IsChecked = SettingsStore.Settings.Interface.Theme == theme, Margin = new Thickness(0, 0, 10, 0) };
            var card = new StackPanel { Width = 150 };
            var icon = new TextBlock { Text = glyph, FontSize = 22, Margin = new Thickness(0, 0, 0, 6) };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            card.Children.Add(icon);
            card.Children.Add(new TextBlock { Text = label });
            rb.Content = card;
            rb.Checked += (_, _) => { SettingsStore.Settings.Interface.Theme = theme; SettingsStore.NotifySettingsChanged(); };
            themes.Children.Add(rb);
        }
        sp.Children.Add(themes);
        return sp;
    }

    private FrameworkElement ImportPage()
    {
        var sp = new StackPanel { Margin = new Thickness(40, 28, 40, 10) };
        sp.Children.Add(Text("Bring your connections", "PageTitle"));
        sp.Children.Add(Text("Import what you already use. You can do this again later from the Sessions sidebar or Settings › Backup & import.", "Body", 6));

        // PuTTY
        var putty = new Border { Margin = new Thickness(0, 18, 0, 0) };
        putty.SetResourceReference(StyleProperty, "Card");
        var pp = new StackPanel();
        pp.Children.Add(Text("PuTTY", "BodyStrong"));
        var names = PuttyImport.SessionNames();
        bool defaults = PuttyImport.HasDefaultSettings();
        if (names.Count == 0 && !defaults)
        {
            pp.Children.Add(Text("No PuTTY settings were found on this PC for your account.", "Caption", 4));
        }
        else
        {
            _puttySessions = new CheckBox { Content = $"Import {names.Count} saved session{(names.Count == 1 ? "" : "s")}", IsChecked = names.Count > 0, IsEnabled = names.Count > 0, Margin = new Thickness(0, 8, 0, 0) };
            _puttyDefaults = new CheckBox { Content = "Use PuTTY's Default Settings (font, colours, keyboard, bell, mouse buttons, proxy…)", IsChecked = false, IsEnabled = defaults, Margin = new Thickness(0, 6, 0, 0) };
            pp.Children.Add(_puttySessions);
            pp.Children.Add(_puttyDefaults);
        }
        putty.Child = pp;
        sp.Children.Add(putty);

        // PuTTY Connection Manager
        var pcm = new Border { Margin = new Thickness(0, 10, 0, 0) };
        pcm.SetResourceReference(StyleProperty, "Card");
        var pg = new Grid();
        pg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pl = new StackPanel();
        pl.Children.Add(Text("PuTTY Connection Manager", "BodyStrong"));
        pl.Children.Add(Text("Import a connection database (.dat or exported .xml). Folders and login macros come along.", "Caption", 4));
        _pcmResult = Text("", "Caption", 4);
        _pcmResult.Visibility = Visibility.Collapsed;
        _pcmResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        pl.Children.Add(_pcmResult);
        var choose = new Button { Content = "Choose file…", Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
        choose.Click += (_, _) =>
        {
            int n = ImportWindow.ImportPcm(this);
            if (n > 0) { _pcmResult.Text = $"Imported {n} session{(n == 1 ? "" : "s")}."; _pcmResult.Visibility = Visibility.Visible; }
        };
        Grid.SetColumn(choose, 1);
        pg.Children.Add(pl);
        pg.Children.Add(choose);
        pcm.Child = pg;
        sp.Children.Add(pcm);

        // FluenTTY export
        var ft = new Border { Margin = new Thickness(0, 10, 0, 0) };
        ft.SetResourceReference(StyleProperty, "Card");
        var fg = new Grid();
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fl = new StackPanel();
        fl.Children.Add(Text("FluenTTY settings file", "BodyStrong"));
        fl.Children.Add(Text("Settings and sessions exported from FluenTTY on another PC.", "Caption", 4));
        var importFile = new Button { Content = "Choose file…", Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
        importFile.Click += (_, _) => SettingsTransfer.ImportInteractive(this);
        Grid.SetColumn(importFile, 1);
        fg.Children.Add(fl);
        fg.Children.Add(importFile);
        ft.Child = fg;
        sp.Children.Add(ft);

        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void RunPuttyImport()
    {
        bool sessions = _puttySessions?.IsChecked == true;
        bool defaults = _puttyDefaults?.IsChecked == true;
        if (sessions) PuttyImport.ImportSessions(overwrite: false);
        if (defaults)
        {
            PuttyImport.ImportDefaultSettings(SettingsStore.Settings);
            SettingsStore.NotifySettingsChanged();
        }
    }

    private FrameworkElement DonePage()
    {
        var sp = new StackPanel { Margin = new Thickness(40, 34, 40, 10) };
        sp.Children.Add(Text("You're all set", "PageTitle"));
        sp.Children.Add(Text("A few things worth knowing:", "Body", 8));
        foreach (var (glyph, text) in new[]
        {
            ("", "Ctrl+Shift+P opens the connection search: type a session name or an address like admin@10.0.0.1."),
            ("", "Saved sessions live in the sidebar (Ctrl+Shift+B). Drag them between folders; double-click to connect."),
            ("", "Right-click a tab (or drag it down) to pop it out into its own window. F11 is full screen."),
            ("", "SSH tabs open an SFTP browser on the left; double-click a file to edit it."),
            ("", "Everything PuTTY lets you configure is in Settings — use the search box to find it."),
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var icon = new TextBlock { Text = glyph, FontSize = 18, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(Text(text, "Body"));
            sp.Children.Add(row);
        }
        return sp;
    }
}
