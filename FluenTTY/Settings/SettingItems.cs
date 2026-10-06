using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluenTTY.Services;
using Microsoft.Win32;

namespace FluenTTY.Settings;

/// <summary>
/// One row on a settings page (a Windows 11 "settings card"): title and description on the left, the
/// control on the right — or full width for list editors. Every item is searchable by its title,
/// description and keywords. Changes apply immediately and are saved shortly after.
/// </summary>
public abstract class SettingItem
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Extra search words (PuTTY's names for the option, synonyms).</summary>
    public string Keywords { get; init; } = "";
    /// <summary>Set when the page is registered (for search result breadcrumbs).</summary>
    public SettingPage? Page { get; set; }

    public virtual bool Matches(string query)
    {
        var hay = $"{Title} {Description} {Keywords} {Page?.Title} {Page?.Group}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public abstract FrameworkElement Build();

    protected static void Changed() => SettingsStore.NotifySettingsChanged();

    protected Border Card(FrameworkElement? control, FrameworkElement? below = null, string? glyph = null)
    {
        var card = new Border();
        card.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCard");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var t = new TextBlock { Text = Title, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        text.Children.Add(t);
        if (!string.IsNullOrEmpty(Description))
        {
            var d = new TextBlock { Text = Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            d.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            text.Children.Add(d);
        }
        grid.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(control, Title);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        if (below != null)
        {
            below.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(below, 1);
            Grid.SetColumnSpan(below, 2);
            grid.Children.Add(below);
        }
        card.Child = grid;
        return card;
    }
}

public sealed class SettingPage
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Group { get; init; } = "";
    public string Description { get; init; } = "";
    public List<SettingItem> Items { get; } = new();

    public SettingPage Add(params SettingItem[] items)
    {
        foreach (var i in items) { i.Page = this; Items.Add(i); }
        return this;
    }
}

/// <summary>Group heading inside a page (not searchable on its own).</summary>
public sealed class HeaderItem : SettingItem
{
    public HeaderItem(string title) { Title = title; }
    public override bool Matches(string query) => false;
    public override FrameworkElement Build()
    {
        var t = new TextBlock { Text = Title };
        t.SetResourceReference(FrameworkElement.StyleProperty, "GroupHeader");
        return t;
    }
}

/// <summary>A paragraph of explanation (InfoBar look).</summary>
public sealed class NoteItem : SettingItem
{
    public NoteItem(string text) { Description = text; }
    public override bool Matches(string query) => false;
    public override FrameworkElement Build()
    {
        var b = new Border { Margin = new Thickness(0, 4, 0, 8) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "InfoBar");
        var t = new TextBlock { Text = Description, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        b.Child = t;
        return b;
    }
}

public sealed class ToggleItem : SettingItem
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    public ToggleItem(string title, string description, Func<bool> get, Action<bool> set, string keywords = "")
    {
        Title = title; Description = description; _get = get; _set = set; Keywords = keywords;
    }
    public override FrameworkElement Build()
    {
        var cb = new CheckBox { IsChecked = _get() };
        cb.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
        cb.Checked += (_, _) => { _set(true); Changed(); };
        cb.Unchecked += (_, _) => { _set(false); Changed(); };
        return Card(cb);
    }
}

public sealed class ChoiceItem<T> : SettingItem where T : notnull
{
    private readonly Func<T> _get;
    private readonly Action<T> _set;
    private readonly (T value, string label)[] _options;
    public ChoiceItem(string title, string description, Func<T> get, Action<T> set, (T, string)[] options, string keywords = "")
    {
        Title = title; Description = description; _get = get; _set = set; _options = options; Keywords = keywords;
    }
    public override bool Matches(string query) => base.Matches(query) || _options.Any(o => o.label.Contains(query, StringComparison.OrdinalIgnoreCase));
    public override FrameworkElement Build()
    {
        var box = new ComboBox { MinWidth = 220, MaxWidth = 340 };
        foreach (var (v, label) in _options) box.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        var current = _get();
        box.SelectedIndex = Math.Max(0, Array.FindIndex(_options, o => o.value.Equals(current)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T v }) { _set(v); Changed(); }
        };
        return Card(box);
    }
}

public sealed class NumberItem : SettingItem
{
    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min, _max;
    private readonly string _unit;
    private readonly bool _integer;
    public NumberItem(string title, string description, Func<double> get, Action<double> set, double min, double max, string unit = "", bool integer = true, string keywords = "")
    {
        Title = title; Description = description; _get = get; _set = set; _min = min; _max = max; _unit = unit; _integer = integer; Keywords = keywords;
    }
    public override FrameworkElement Build()
    {
        var box = new TextBox { Width = 110, Text = Format(_get()), TextAlignment = TextAlignment.Right };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        if (_unit.Length > 0)
        {
            var u = new TextBlock { Text = _unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 50 };
            u.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            panel.Children.Add(u);
        }
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                v = Math.Clamp(_integer ? Math.Round(v) : v, _min, _max);
                if (v != _get()) { _set(v); Changed(); }
            }
            box.Text = Format(_get());
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        box.ToolTip = $"{Format(_min)}–{Format(_max)}";
        return Card(panel);
    }
    private string Format(double v) => _integer ? ((long)v).ToString(CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);
}

public sealed class TextItem : SettingItem
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly bool _multiline, _mono, _wide;
    public TextItem(string title, string description, Func<string> get, Action<string> set, bool multiline = false, bool mono = false, bool wide = false, string keywords = "")
    {
        Title = title; Description = description; _get = get; _set = set; _multiline = multiline; _mono = mono; _wide = wide; Keywords = keywords;
    }
    public override FrameworkElement Build()
    {
        var box = new TextBox { Text = _get() };
        if (_mono) box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        if (_multiline) { box.AcceptsReturn = true; box.MinHeight = 70; box.TextWrapping = TextWrapping.NoWrap; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        box.LostKeyboardFocus += (_, _) => { if (box.Text != _get()) { _set(box.Text); Changed(); } };
        if (_multiline || _wide) return Card(null, box);
        box.Width = 280;
        return Card(box);
    }
}

public sealed class PasswordItem : SettingItem
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    public PasswordItem(string title, string description, Func<string> get, Action<string> set)
    {
        Title = title; Description = description; _get = get; _set = set;
    }
    public override FrameworkElement Build()
    {
        var box = new PasswordBox { Width = 280, Password = SecretStore.Unprotect(_get()) ?? "" };
        box.LostKeyboardFocus += (_, _) => { _set(box.Password.Length == 0 ? "" : SecretStore.Protect(box.Password) ?? ""); Changed(); };
        return Card(box);
    }
}

public sealed class PathItem : SettingItem
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly string _filter;
    private readonly bool _folder, _save;
    public PathItem(string title, string description, Func<string> get, Action<string> set, string filter = "All files|*.*", bool folder = false, bool save = false, string keywords = "")
    {
        Title = title; Description = description; _get = get; _set = set; _filter = filter; _folder = folder; _save = save; Keywords = keywords;
    }
    public override FrameworkElement Build()
    {
        var box = new TextBox { Text = _get() };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        var dock = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        box.LostKeyboardFocus += (_, _) => { if (box.Text != _get()) { _set(box.Text.Trim()); Changed(); } };
        browse.Click += (_, _) =>
        {
            string? chosen = null;
            var initial = Environment.ExpandEnvironmentVariables(box.Text);
            if (_folder)
            {
                var d = new OpenFolderDialog { Title = Title };
                if (Directory.Exists(initial)) d.InitialDirectory = initial;
                if (d.ShowDialog() == true) chosen = d.FolderName;
            }
            else if (_save)
            {
                var d = new SaveFileDialog { Filter = _filter, Title = Title, FileName = Path.GetFileName(initial) };
                if (d.ShowDialog() == true) chosen = d.FileName;
            }
            else
            {
                var d = new OpenFileDialog { Filter = _filter, Title = Title };
                if (File.Exists(initial)) d.FileName = initial;
                if (d.ShowDialog() == true) chosen = d.FileName;
            }
            if (chosen == null) return;
            box.Text = chosen;
            _set(chosen);
            Changed();
        };
        return Card(null, dock);
    }
}

/// <summary>A card with a button (import, export, reset...).</summary>
public sealed class ActionItem : SettingItem
{
    private readonly string _button;
    private readonly Action<Window?> _action;
    private readonly bool _accent;
    public ActionItem(string title, string description, string button, Action<Window?> action, bool accent = false, string keywords = "")
    {
        Title = title; Description = description; _button = button; _action = action; _accent = accent; Keywords = keywords;
    }
    public override FrameworkElement Build()
    {
        var b = new Button { Content = _button, Padding = new Thickness(14, 5, 14, 5), MinWidth = 120 };
        if (_accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        b.Click += (_, _) => _action(Window.GetWindow(b));
        return Card(b);
    }
}

/// <summary>Anything else: the page supplies the control (list editors, scheme editor...).</summary>
public sealed class CustomItem : SettingItem
{
    private readonly Func<FrameworkElement> _build;
    private readonly bool _wide;
    public CustomItem(string title, string description, Func<FrameworkElement> build, bool wide = true, string keywords = "")
    {
        Title = title; Description = description; _build = build; _wide = wide; Keywords = keywords;
    }
    public override FrameworkElement Build() => _wide ? Card(null, _build()) : Card(_build());
}
