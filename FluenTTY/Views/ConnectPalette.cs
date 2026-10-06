using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>
/// "Select a session or enter an address" — a search palette in the style of Tabby's profile selector:
/// recent connections, saved sessions (fuzzy search on name, host, user and folder), and one
/// "Connect to …" row per protocol for whatever was typed.
/// </summary>
public sealed class ConnectPalette : UserControl
{
    private sealed class Row
    {
        public string Title = "";
        public string Subtitle = "";
        public string Glyph = "";
        public Action? Run;
        public bool IsHeader => Run == null;
    }

    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Border _panel;
    private List<Row> _rows = new();

    public event Action<SessionProfile?>? Chosen;
    public event Action<string>? NewSessionRequested;
    public event Action? ManageRequested;

    public bool IsOpen => Visibility == Visibility.Visible;

    public ConnectPalette()
    {
        // Strong enough that terminal text behind the palette recedes instead of reading as part of it.
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
        dim.MouseLeftButtonDown += (_, _) => Close();

        _search = new TextBox { FontSize = 15, Padding = new Thickness(36, 8, 8, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search sessions or enter an address");
        _search.TextChanged += (_, _) => Rebuild();
        _search.PreviewKeyDown += OnSearchKey;
        var searchIcon = new TextBlock { Text = "", FontSize = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var hint = new TextBlock { Text = "Select a session or enter an address (user@host:port, COM3, ssh://…)", Margin = new Thickness(38, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(hint);

        _list = new ListBox { MaxHeight = 420, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4) };
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "RowItem");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem item && item.Tag is Row r && !r.IsHeader)
                Execute(r);
        };

        var stack = new DockPanel();
        DockPanel.SetDock(searchGrid, Dock.Top);
        stack.Children.Add(searchGrid);
        stack.Children.Add(_list);

        _panel = new Border
        {
            Width = 640,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = stack,
            RenderTransform = new TranslateTransform(),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.28, Direction = 270 },
        };
        _panel.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        _panel.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        var root = new Grid();
        root.Children.Add(dim);
        root.Children.Add(_panel);
        Content = root;
    }

    public void Open(string text = "")
    {
        Visibility = Visibility.Visible;
        _search.Text = text;
        Rebuild();
        if (ThemeService.AnimationsEnabled)
        {
            _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ThemeService.Anim(160)));
            ((TranslateTransform)_panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-12, 0, ThemeService.Anim(200)) { EasingFunction = ThemeService.EaseOut });
        }
        Dispatcher.BeginInvoke(() => { _search.Focus(); _search.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        (Window.GetWindow(this) as MainWindow)?.Dispatcher.BeginInvoke(() => { }); // focus returns via the selected tab's OnActivated
    }

    private void Execute(Row r)
    {
        Close();
        r.Run?.Invoke();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(8); e.Handled = true; break;
            case Key.PageUp: Move(-8); e.Handled = true; break;
            case Key.Enter:
                if (_list.SelectedItem is ListBoxItem { Tag: Row r } && !r.IsHeader) Execute(r);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        int i = _list.SelectedIndex;
        int n = _list.Items.Count;
        if (n == 0) return;
        int step = Math.Sign(delta);
        for (int moved = 0; moved < Math.Abs(delta);)
        {
            int next = i + step;
            if (next < 0 || next >= n) break;
            i = next;
            if (((ListBoxItem)_list.Items[i]).Tag is Row { IsHeader: false }) moved++;
        }
        if (((ListBoxItem)_list.Items[Math.Clamp(i, 0, n - 1)]).Tag is Row { IsHeader: false })
        {
            _list.SelectedIndex = i;
            _list.ScrollIntoView(_list.SelectedItem);
        }
    }

    private void Rebuild()
    {
        var text = _search.Text.Trim();
        var rows = new List<Row>();
        var owner = () => Window.GetWindow(this) as MainWindow;

        void Add(string group, IEnumerable<Row> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            rows.Add(new Row { Title = group });
            rows.AddRange(list);
        }

        Row ForProfile(SessionProfile p, string? note = null) => new()
        {
            Title = string.IsNullOrEmpty(p.Name) ? p.DisplayTarget : p.Name,
            Subtitle = $"{SessionProfile.ProtocolName(p.Protocol)} · {p.DisplayTarget}{(string.IsNullOrEmpty(p.Folder) ? "" : " · " + p.Folder)}{note}",
            Glyph = SessionProfile.ProtocolGlyph(p.Protocol),
            Run = () => Chosen?.Invoke(p),
        };

        var saved = SettingsStore.Sessions.Sessions;
        if (text.Length == 0)
        {
            Add("Recent", SettingsStore.Sessions.Recent.Take(6).Select(p => ForProfile(p)));
            Add("Saved sessions", saved.OrderBy(s => s.Folder).ThenBy(s => s.Name).Take(200).Select(p => ForProfile(p)));
        }
        else
        {
            var scored = saved.Select(s => (s, score: Score(s, text))).Where(x => x.score > 0).OrderByDescending(x => x.score).Take(50).Select(x => ForProfile(x.s));
            Add("Saved sessions", scored);

            var quick = new List<Row>();
            bool isCom = System.Text.RegularExpressions.Regex.IsMatch(text, @"^COM\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var protocols = isCom ? new[] { ProtocolKind.Serial } : text.Contains("://") ? new[] { ProtocolKind.Ssh } : StartTab.Protocols.Where(p => p != ProtocolKind.Serial).ToArray();
            foreach (var proto in protocols)
            {
                var prof = QuickConnect.Parse(text, proto);
                if (prof == null) continue;
                if (!text.Contains("://") && !isCom) prof.Protocol = proto;
                quick.Add(new Row
                {
                    Title = $"Connect to \"{text}\"",
                    Subtitle = SessionProfile.ProtocolName(prof.Protocol),
                    Glyph = "",
                    Run = () => Chosen?.Invoke(prof),
                });
            }
            Add("Quick connect", quick);
        }

        Add("Actions", new[]
        {
            new Row { Title = text.Length > 0 ? $"New session for \"{text}\"…" : "New session…", Subtitle = "Ctrl+Shift+N", Glyph = "", Run = () => NewSessionRequested?.Invoke(text) },
            new Row { Title = "Manage saved sessions", Subtitle = "Show the sessions sidebar", Glyph = "", Run = () => ManageRequested?.Invoke() },
        });

        _rows = rows;
        _list.Items.Clear();
        foreach (var r in rows) _list.Items.Add(Render(r));
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (((ListBoxItem)_list.Items[i]).Tag is Row { IsHeader: false }) { _list.SelectedIndex = i; break; }
        }
    }

    /// <summary>Fuzzy score: whole-word/prefix hits rank above scattered subsequence matches.</summary>
    private static int Score(SessionProfile p, string q)
    {
        string hay = $"{p.Name} {p.Host} {p.Username} {p.Folder} {SessionProfile.ProtocolName(p.Protocol)} {p.Notes}".ToLowerInvariant();
        q = q.ToLowerInvariant();
        if (p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        if (p.Host.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 90;
        int idx = hay.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 70 - Math.Min(idx, 40);
        // every token must appear somewhere
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1 && tokens.All(t => hay.Contains(t))) return 30;
        // subsequence
        int j = 0;
        foreach (var ch in hay) if (j < q.Length && ch == q[j]) j++;
        return j == q.Length ? 10 : 0;
    }

    private static ListBoxItem Render(Row r)
    {
        var item = new ListBoxItem { Tag = r };
        if (r.IsHeader)
        {
            var h = new TextBlock { Text = r.Title, Margin = new Thickness(2, 8, 0, 2), FontWeight = FontWeights.SemiBold, FontSize = 12 };
            h.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            item.Content = h;
            item.IsEnabled = false;
            item.MinHeight = 24;
            return item;
        }
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = r.Glyph, FontSize = 15, Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var title = new TextBlock { Text = r.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(StyleProperty, "Body");
        var sub = new TextBlock { Text = r.Subtitle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        sub.SetResourceReference(StyleProperty, "Caption");
        Grid.SetColumn(title, 1);
        Grid.SetColumn(sub, 2);
        g.Children.Add(icon);
        g.Children.Add(title);
        g.Children.Add(sub);
        item.Content = g;
        System.Windows.Automation.AutomationProperties.SetName(item, $"{r.Title} {r.Subtitle}");
        return item;
    }
}
