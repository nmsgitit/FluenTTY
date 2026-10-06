using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>The new-tab page: quick connect, recent connections and saved sessions.</summary>
public sealed class StartTab : SessionTab
{
    private readonly ComboBox _protocol;
    private readonly TextBox _host;
    private readonly TextBlock _placeholder;
    private readonly WrapPanel _recent;
    private readonly WrapPanel _saved;
    private readonly StackPanel _recentSection;
    private readonly StackPanel _savedSection;
    private readonly WrapPanel _ports;
    private readonly Border _importBanner;

    public static readonly ProtocolKind[] Protocols =
    {
        ProtocolKind.Ssh, ProtocolKind.Sftp, ProtocolKind.Telnet, ProtocolKind.Serial, ProtocolKind.Rdp, ProtocolKind.Vnc,
        ProtocolKind.Ftp, ProtocolKind.Rlogin, ProtocolKind.Rsh, ProtocolKind.Raw, ProtocolKind.Xdmcp,
    };

    public StartTab()
    {
        Title = "New tab";
        Glyph = "";
        State = TabState.Idle;

        var page = new StackPanel { MaxWidth = 920, Margin = new Thickness(40, 36, 40, 40), HorizontalAlignment = HorizontalAlignment.Stretch };

        // Hero
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 24) };
        hero.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/FluenTTY.png")), Width = 52, Height = 52 });
        var heroText = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "FluenTTY" };
        title.SetResourceReference(StyleProperty, "PageTitle");
        var sub = new TextBlock { Text = "Connect to a server, a device on a serial port, or a remote desktop." };
        sub.SetResourceReference(StyleProperty, "Caption");
        sub.FontSize = 13;
        heroText.Children.Add(title);
        heroText.Children.Add(sub);
        hero.Children.Add(heroText);
        page.Children.Add(hero);

        // Quick connect card
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        card.Padding = new Thickness(20, 16, 20, 16);
        var qc = new StackPanel();
        var qcTitle = new TextBlock { Text = "Quick connect", Margin = new Thickness(0, 0, 0, 10) };
        qcTitle.SetResourceReference(StyleProperty, "BodyStrong");
        qc.Children.Add(qcTitle);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _protocol = new ComboBox { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var p in Protocols) _protocol.Items.Add(new ComboBoxItem { Content = SessionProfile.ProtocolName(p), Tag = p });
        _protocol.SelectedIndex = 0;
        _protocol.SelectionChanged += (_, _) => UpdatePlaceholder();
        System.Windows.Automation.AutomationProperties.SetName(_protocol, "Protocol");
        row.Children.Add(_protocol);

        var hostGrid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        _host = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(_host, "Host");
        _host.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ConnectNow(); e.Handled = true; } };
        _host.TextChanged += (_, _) => _placeholder!.Visibility = _host.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder = new TextBlock { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        hostGrid.Children.Add(_host);
        hostGrid.Children.Add(_placeholder);
        Grid.SetColumn(hostGrid, 1);
        row.Children.Add(hostGrid);

        var connect = new Button { Content = "Connect", Padding = new Thickness(20, 6, 20, 6), MinWidth = 100 };
        connect.SetResourceReference(StyleProperty, "AccentButtonStyle");
        connect.Click += (_, _) => ConnectNow();
        Grid.SetColumn(connect, 2);
        row.Children.Add(connect);
        qc.Children.Add(row);

        _ports = new WrapPanel { Margin = new Thickness(148, 8, 0, 0), Visibility = Visibility.Collapsed };
        qc.Children.Add(_ports);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 10, 0, 0) };
        links.Children.Add(Link("New session…", "", () => EditMore(new SessionProfile())));
        links.Children.Add(Link("More options…", "", () => EditMore(ParseInput() ?? new SessionProfile { Protocol = SelectedProtocol })));
        links.Children.Add(Link("Search sessions  Ctrl+Shift+P", "", () => (Window.GetWindow(this) as MainWindow)?.Palette.Open()));
        qc.Children.Add(links);
        card.Child = qc;
        page.Children.Add(card);

        // PuTTY import banner
        _importBanner = new Border { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        _importBanner.SetResourceReference(StyleProperty, "InfoBar");
        page.Children.Add(_importBanner);

        _recent = new WrapPanel();
        _recentSection = Section("Recent", _recent);
        page.Children.Add(_recentSection);
        _saved = new WrapPanel();
        _savedSection = Section("Saved sessions", _saved);
        page.Children.Add(_savedSection);

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdatePlaceholder();
        Refresh();
        SettingsStore.SessionsChanged += Refresh;
        Unloaded += (_, _) => { };
    }

    public override bool CanDuplicate => false;
    public override bool IsLive => false;
    public override string Description => "New tab";

    private ProtocolKind SelectedProtocol => (ProtocolKind)((ComboBoxItem)_protocol.SelectedItem).Tag;

    private void UpdatePlaceholder()
    {
        var p = SelectedProtocol;
        _placeholder.Text = p switch
        {
            ProtocolKind.Serial => "COM port, e.g. COM3",
            ProtocolKind.Rdp or ProtocolKind.Vnc or ProtocolKind.Xdmcp => "host or host:port",
            _ => "user@host:port",
        };
        _ports.Children.Clear();
        if (p == ProtocolKind.Serial)
        {
            string[] names;
            try { names = SerialPort.GetPortNames().Distinct().OrderBy(n => n.Length).ThenBy(n => n).ToArray(); }
            catch { names = Array.Empty<string>(); }
            if (names.Length == 0)
            {
                var none = new TextBlock { Text = "No COM ports found on this PC." };
                none.SetResourceReference(StyleProperty, "Caption");
                _ports.Children.Add(none);
            }
            foreach (var n in names)
            {
                var chip = new Button { Content = n, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
                chip.Click += (_, _) => { _host.Text = n; ConnectNow(); };
                _ports.Children.Add(chip);
            }
            _ports.Visibility = Visibility.Visible;
        }
        else _ports.Visibility = Visibility.Collapsed;
    }

    private SessionProfile? ParseInput() => QuickConnect.Parse(_host.Text, SelectedProtocol) is { } p ? Fix(p) : null;

    private SessionProfile Fix(SessionProfile p)
    {
        // A typed URL decides its own protocol; otherwise the combo box does.
        if (!_host.Text.Contains("://") && p.Protocol != ProtocolKind.Serial) p.Protocol = SelectedProtocol;
        return p;
    }

    private void ConnectNow()
    {
        var p = ParseInput();
        if (p == null) { _host.Focus(); return; }
        (Window.GetWindow(this) as MainWindow)?.OpenSession(p);
    }

    private void EditMore(SessionProfile p)
    {
        var owner = Window.GetWindow(this) as MainWindow;
        var result = SessionEditorWindow.Edit(owner, p, isNew: true);
        if (result.Profile == null) return;
        if (result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
        if (result.Connect) owner?.OpenSession(result.Profile);
    }

    private Button Link(string text, string glyph, Action a)
    {
        var b = new Button { Content = text, Tag = glyph };
        b.SetResourceReference(StyleProperty, "ToolbarButton");
        b.Click += (_, _) => a();
        return b;
    }

    private static StackPanel Section(string title, Panel content)
    {
        var s = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
        var t = new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 10) };
        t.SetResourceReference(StyleProperty, "BodyStrong");
        s.Children.Add(t);
        s.Children.Add(content);
        return s;
    }

    private void Refresh()
    {
        _recent.Children.Clear();
        foreach (var r in SettingsStore.Sessions.Recent.Take(8)) _recent.Children.Add(SessionCard(r, recent: true));
        _recentSection.Visibility = _recent.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _saved.Children.Clear();
        foreach (var s in SettingsStore.Sessions.Sessions.OrderByDescending(s => s.LastUsed ?? DateTime.MinValue).ThenBy(s => s.Name).Take(12))
            _saved.Children.Add(SessionCard(s, recent: false));
        _savedSection.Visibility = _saved.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateImportBanner();
    }

    private void UpdateImportBanner()
    {
        var names = PuttyImport.SessionNames();
        bool alreadyImported = names.Count > 0 && names.All(n => SettingsStore.Sessions.Sessions.Any(s => s.Name == n));
        if (names.Count == 0 || alreadyImported || SettingsStore.Settings.Interface.Startup == StartupAction.Nothing)
        {
            _importBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = $"Found {names.Count} saved PuTTY session{(names.Count == 1 ? "" : "s")} on this PC.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(StyleProperty, "Body");
        var button = new Button { Content = "Import from PuTTY", Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) =>
        {
            int n = PuttyImport.ImportSessions(overwrite: false);
            FluentDialog.Info(Window.GetWindow(this), "Import from PuTTY", n == 0 ? "No new sessions to import." : $"Imported {n} session{(n == 1 ? "" : "s")}. They're in the sidebar under \"PuTTY\".");
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(text);
        grid.Children.Add(button);
        _importBanner.Child = grid;
        _importBanner.Visibility = Visibility.Visible;
    }

    private Button SessionCard(SessionProfile p, bool recent)
    {
        // A real Button (Enter/Space, screen readers, UI Automation) drawn as a Fluent card.
        var b = new Button
        {
            Width = 206, Height = 76, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Padding = new Thickness(14, 10, 14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(TemplateProperty, "SubtleButtonTemplate");
        b.SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        b.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock { Text = SessionProfile.ProtocolGlyph(p.Protocol), FontSize = 20, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var texts = new StackPanel();
        var name = new TextBlock { Text = string.IsNullOrEmpty(p.Name) ? p.DisplayTarget : p.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(StyleProperty, "BodyStrong");
        var target = new TextBlock { Text = p.DisplayTarget, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        target.SetResourceReference(StyleProperty, "Caption");
        var when = new TextBlock
        {
            Text = SessionProfile.ProtocolName(p.Protocol) + (p.LastUsed.HasValue ? "  ·  " + Ago(p.LastUsed.Value) : ""),
            Margin = new Thickness(0, 2, 0, 0),
        };
        when.SetResourceReference(StyleProperty, "Caption");
        texts.Children.Add(name);
        texts.Children.Add(target);
        texts.Children.Add(when);
        Grid.SetColumn(texts, 1);
        g.Children.Add(icon);
        g.Children.Add(texts);
        b.Content = g;
        b.ToolTip = string.IsNullOrEmpty(p.Notes) ? p.DisplayTarget : p.Notes;
        System.Windows.Automation.AutomationProperties.SetName(b, $"Connect to {name.Text}");

        b.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenSession(p);

        var menu = new ContextMenu();
        var connect = new MenuItem { Header = "Connect" };
        connect.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenSession(p);
        var popout = new MenuItem { Header = "Connect in a new window" };
        popout.Click += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenSession(p, popOut: true);
        menu.Items.Add(connect);
        menu.Items.Add(popout);
        if (recent)
        {
            var save = new MenuItem { Header = "Save as session…" };
            save.Click += (_, _) =>
            {
                var copy = p.Clone();
                copy.Id = Guid.NewGuid();
                copy.Name = copy.DisplayTarget;
                var result = SessionEditorWindow.Edit(Window.GetWindow(this), copy, isNew: true);
                if (result.Profile != null && result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
            };
            var remove = new MenuItem { Header = "Remove from recent" };
            remove.Click += (_, _) => { SettingsStore.Sessions.Recent.Remove(p); SettingsStore.SaveSessions(); };
            menu.Items.Add(save);
            menu.Items.Add(remove);
        }
        else
        {
            var edit = new MenuItem { Header = "Edit…" };
            edit.Click += (_, _) =>
            {
                var result = SessionEditorWindow.Edit(Window.GetWindow(this), p.Clone(), isNew: false);
                if (result.Profile != null && result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
                if (result.Profile != null && result.Connect) (Window.GetWindow(this) as MainWindow)?.OpenSession(result.Profile);
            };
            menu.Items.Add(edit);
        }
        b.ContextMenu = menu;
        return b;
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return t.ToString("d MMM");
    }

    public override void OnActivated()
    {
        Dispatcher.BeginInvoke(() => _host.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        Refresh();
    }

    public override void Shutdown() => SettingsStore.SessionsChanged -= Refresh;
}
