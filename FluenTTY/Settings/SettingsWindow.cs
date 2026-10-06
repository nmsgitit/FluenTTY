using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FluenTTY.Services;
using FluenTTY.Settings;

namespace FluenTTY.Views;

/// <summary>
/// Settings in its own window: categorised navigation on the left (grouped like PuTTY's tree), a search box
/// that finds any option across all pages, and Windows 11-style setting cards that apply immediately.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    public static void ShowSingle(Window? owner, string? page = null)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            if (page != null) _open.Navigate(page);
            return;
        }
        _open = new SettingsWindow { Owner = owner };
        _open.Closed += (_, _) => { _open = null; SettingsStore.SaveSettingsNow(); };
        _open.Show();
        if (page != null) _open.Navigate(page);
    }

    private List<SettingPage> _pages = new();
    private readonly ListBox _nav;
    private readonly TextBox _search;
    private readonly StackPanel _content;
    private readonly ScrollViewer _scroll;
    private readonly Rectangle _pill;
    private readonly TranslateTransform _pillOffset = new();
    private SettingPage? _current;
    private bool _ready;

    private SettingsWindow()
    {
        Title = "Settings — FluenTTY";
        Width = 1080;
        Height = 760;
        MinWidth = 760;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/FluenTTY.ico"));
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);
        UseLayoutRounding = true;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: title, search, nav
        var left = new DockPanel { Margin = new Thickness(12, 16, 8, 12) };
        var heading = new TextBlock { Text = "Settings", Margin = new Thickness(8, 0, 0, 14) };
        heading.SetResourceReference(StyleProperty, "SectionTitle");
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);

        _search = new TextBox { Padding = new Thickness(32, 6, 8, 6) };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Find a setting");
        var searchIcon = new TextBlock { Text = "", FontSize = 13, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        searchIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var hint = new TextBlock { Text = "Find a setting", Margin = new Thickness(33, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        searchGrid.Children.Add(_search);
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(hint);
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; OnSearch(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) _search.Clear(); };
        DockPanel.SetDock(searchGrid, Dock.Top);
        left.Children.Add(searchGrid);

        _nav = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_nav, ScrollBarVisibility.Disabled);
        // ~35 rows: keep them all realised so UI Automation (screen readers, tests) sees every page.
        VirtualizingPanel.SetIsVirtualizing(_nav, false);
        System.Windows.Automation.AutomationProperties.SetName(_nav, "Settings categories");
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: SettingPage p })
            {
                _current = p;
                if (_search.Text.Length > 0) _search.Text = ""; // shows _current via OnSearch
                else ShowPage(p, animate: _ready);
            }
        };
        _pill = new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, RenderTransform = _pillOffset };
        _pill.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        var navGrid = new Grid();
        navGrid.Children.Add(_nav);
        navGrid.Children.Add(_pill);
        left.Children.Add(navGrid);
        root.Children.Add(left);

        // Right: page
        _content = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 960, RenderTransform = new TranslateTransform() };
        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _scroll.ScrollChanged += (_, _) => MovePill(false);
        var card = new Border { Child = _scroll, CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 8, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid.SetColumn(card, 1);
        root.Children.Add(card);
        Content = root;

        Reload();
        Loaded += (_, _) => { MovePill(false); _ready = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && _search.Text.Length == 0) Close();
        };
    }

    /// <summary>Rebuilds all pages (after import/reset, when the settings object was replaced).</summary>
    public void Reload(bool keepPage = true)
    {
        var key = keepPage ? _current?.Key : null;
        _pages = SettingsCatalog.Build();
        _nav.Items.Clear();
        string? group = null;
        foreach (var p in _pages)
        {
            if (p.Group != group)
            {
                group = p.Group;
                var h = new TextBlock { Text = group, Margin = new Thickness(10, _nav.Items.Count == 0 ? 2 : 14, 0, 4), FontSize = 12, FontWeight = FontWeights.SemiBold };
                h.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                _nav.Items.Add(new ListBoxItem { Content = h, IsEnabled = false, Focusable = false, MinHeight = 0, Padding = new Thickness(0) });
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 34 };
            var icon = new TextBlock { Text = p.Glyph, FontSize = 15, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var title = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(StyleProperty, "Body");
            row.Children.Add(icon);
            row.Children.Add(title);
            var item = new ListBoxItem { Content = row, Tag = p };
            item.SetResourceReference(StyleProperty, "NavItem");
            System.Windows.Automation.AutomationProperties.SetName(item, p.Title);
            _nav.Items.Add(item);
        }
        Navigate(key ?? _pages[0].Key);
    }

    public void Navigate(string key)
    {
        var page = _pages.FirstOrDefault(p => p.Key == key) ?? _pages[0];
        _search.Text = "";
        foreach (var item in _nav.Items)
            if (item is ListBoxItem { Tag: SettingPage p } li && p == page) { _nav.SelectedItem = li; li.BringIntoView(); break; }
        ShowPage(page, animate: false);
    }

    private void ShowPage(SettingPage page, bool animate)
    {
        _current = page;
        _content.Children.Clear();
        var title = new TextBlock { Text = page.Title };
        title.SetResourceReference(StyleProperty, "PageTitle");
        _content.Children.Add(title);
        if (!string.IsNullOrEmpty(page.Description))
        {
            var d = new TextBlock { Text = page.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
            d.SetResourceReference(StyleProperty, "Caption");
            d.FontSize = 13;
            _content.Children.Add(d);
        }
        foreach (var item in page.Items)
        {
            try { _content.Children.Add(item.Build()); }
            catch (Exception ex) { Diagnostics.Log($"settings item '{item.Title}' failed: {ex}"); }
        }
        _scroll.ScrollToTop();
        MovePill(animate);
        if (animate) PlayEntrance();
    }

    private void OnSearch()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0)
        {
            if (_current != null) ShowPage(_current, animate: false);
            _pill.Visibility = Visibility.Visible;
            return;
        }
        _pill.Visibility = Visibility.Hidden;
        _content.Children.Clear();
        var title = new TextBlock { Text = $"Results for \"{q}\"" };
        title.SetResourceReference(StyleProperty, "PageTitle");
        _content.Children.Add(title);
        int count = 0;
        foreach (var page in _pages)
        {
            var hits = page.Items.Where(i => i.Matches(q)).ToList();
            if (hits.Count == 0) continue;
            var link = new Button { Content = $"{page.Group}  ›  {page.Title}", Margin = new Thickness(-4, 18, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            link.SetResourceReference(StyleProperty, "LinkButton");
            var p = page;
            link.Click += (_, _) => Navigate(p.Key);
            _content.Children.Add(link);
            foreach (var h in hits)
            {
                try { _content.Children.Add(h.Build()); count++; }
                catch (Exception ex) { Diagnostics.Log($"settings item '{h.Title}' failed: {ex}"); }
            }
        }
        if (count == 0)
        {
            var none = new TextBlock { Text = "No settings match. Try another word — PuTTY's names for options work too.", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(StyleProperty, "Body");
            _content.Children.Add(none);
        }
        _scroll.ScrollToTop();
    }

    private void MovePill(bool animate)
    {
        if (_nav.SelectedItem is not ListBoxItem item || !item.IsVisible) { _pill.Opacity = 0; return; }
        _pill.Opacity = _search.Text.Length == 0 ? 1 : 0;
        var y = item.TranslatePoint(new Point(0, 0), _nav).Y + (item.ActualHeight - _pill.Height) / 2;
        if (!animate || !ThemeService.AnimationsEnabled)
        {
            _pillOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _pillOffset.Y = y;
            return;
        }
        _pillOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, ThemeService.Anim(250)) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlayEntrance()
    {
        if (!ThemeService.AnimationsEnabled) return;
        var d = ThemeService.Anim(280);
        _content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ThemeService.EaseOut });
        ((TranslateTransform)_content.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, d) { EasingFunction = ThemeService.EaseOut });
    }
}
