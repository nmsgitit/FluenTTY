using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Views;

namespace FluenTTY.Settings;

/// <summary>Editors for list-shaped settings (algorithm order, host keys, tunnels, environment) and colour schemes.</summary>
public static class SettingsEditors
{
    private static void Changed() => SettingsStore.NotifySettingsChanged();

    private static Button SmallButton(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    // ------------------------------------------------------------------ SSH algorithm preference (PuTTY's up/down lists)

    public static FrameworkElement AlgorithmList(Func<List<AlgorithmPreference>> get, Action<List<AlgorithmPreference>> set, List<string> defaults)
    {
        var items = new ObservableCollection<AlgorithmPreference>();
        var saved = get();
        foreach (var p in saved.Where(p => defaults.Contains(p.Name))) items.Add(new AlgorithmPreference { Name = p.Name, Enabled = p.Enabled });
        foreach (var d in defaults.Where(d => items.All(i => i.Name != d))) items.Add(new AlgorithmPreference { Name = d, Enabled = true });

        var list = new ListBox { MaxHeight = 240, MinHeight = 120 };
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "RowItem");
        void Save() { set(items.Select(i => new AlgorithmPreference { Name = i.Name, Enabled = i.Enabled }).ToList()); Changed(); }
        void Render(int select = -1)
        {
            list.Items.Clear();
            foreach (var i in items)
            {
                var item = i;
                var cb = new CheckBox { Content = item.Name, IsChecked = item.Enabled, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                cb.Checked += (_, _) => { item.Enabled = true; Save(); };
                cb.Unchecked += (_, _) => { item.Enabled = false; Save(); };
                list.Items.Add(cb);
            }
            if (select >= 0) list.SelectedIndex = select;
        }
        Render();
        var up = SmallButton("Up", () =>
        {
            int i = list.SelectedIndex;
            if (i <= 0) return;
            items.Move(i, i - 1); Save(); Render(i - 1);
        });
        var down = SmallButton("Down", () =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= items.Count - 1) return;
            items.Move(i, i + 1); Save(); Render(i + 1);
        });
        var reset = SmallButton("Reset to defaults", () =>
        {
            items.Clear();
            foreach (var d in defaults) items.Add(new AlgorithmPreference { Name = d, Enabled = true });
            set(new List<AlgorithmPreference>()); Changed(); Render();
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(reset);
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        return panel;
    }

    // ------------------------------------------------------------------ known host keys

    public static FrameworkElement KnownHosts()
    {
        var list = new ListView { MaxHeight = 280, MinHeight = 100 };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Host", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostKey.Host)), Width = 170 });
        view.Columns.Add(new GridViewColumn { Header = "Port", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostKey.Port)), Width = 60 });
        view.Columns.Add(new GridViewColumn { Header = "Key type", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostKey.Algorithm)), Width = 130 });
        view.Columns.Add(new GridViewColumn { Header = "SHA256 fingerprint", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KnownHostKey.FingerprintSha256)), Width = 380 });
        list.View = view;
        void Refresh() => list.ItemsSource = SettingsStore.HostKeys.OrderBy(k => k.Host).ThenBy(k => k.Port).ToList();
        Refresh();
        var remove = SmallButton("Remove selected", () =>
        {
            var sel = list.SelectedItems.Cast<KnownHostKey>().ToList();
            if (sel.Count == 0) return;
            if (!FluentDialog.Confirm(Window.GetWindow(list), "Forget host keys?", $"Forget {sel.Count} host key(s)? You'll be asked to verify those servers again next time.", "Forget")) return;
            foreach (var k in sel) SettingsStore.HostKeys.Remove(k);
            SettingsStore.SaveHostKeys();
            Refresh();
        });
        var copy = SmallButton("Copy fingerprint", () =>
        {
            if (list.SelectedItem is KnownHostKey k) Terminal.TerminalCanvas.TrySetClipboard(k.FingerprintSha256);
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(remove); buttons.Children.Add(copy);
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        return panel;
    }

    // ------------------------------------------------------------------ string lists (manual host keys)

    public static FrameworkElement StringList(Func<List<string>> get, string placeholderHelp)
    {
        var list = new ListBox { MaxHeight = 160, MinHeight = 60, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        void Refresh() { list.ItemsSource = null; list.ItemsSource = get(); }
        Refresh();
        var box = new TextBox { FontFamily = new FontFamily("Cascadia Mono, Consolas"), ToolTip = placeholderHelp };
        System.Windows.Automation.AutomationProperties.SetName(box, placeholderHelp);
        var add = SmallButton("Add", () =>
        {
            var v = box.Text.Trim();
            if (v.Length == 0) return;
            if (!v.StartsWith("SHA256:")) v = "SHA256:" + v;
            get().Add(v); box.Clear(); Changed(); Refresh();
        });
        var remove = SmallButton("Remove", () => { if (list.SelectedItem is string s) { get().Remove(s); Changed(); Refresh(); } });
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(6, 0, 0, 0); remove.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(remove); row.Children.Add(add); row.Children.Add(box);
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(row);
        return panel;
    }

    // ------------------------------------------------------------------ port forwards

    public static FrameworkElement PortForwards(Func<List<PortForward>> get)
    {
        var list = new ListBox { MaxHeight = 180, MinHeight = 60 };
        void Refresh() { list.ItemsSource = null; list.ItemsSource = get(); }
        Refresh();
        var kind = new ComboBox { Width = 110 };
        kind.Items.Add("Local"); kind.Items.Add("Remote"); kind.Items.Add("Dynamic");
        kind.SelectedIndex = 0;
        var src = new TextBox { Width = 110, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Source port, or address:port" };
        var dst = new TextBox { Margin = new Thickness(6, 0, 0, 0), ToolTip = "Destination host:port" };
        System.Windows.Automation.AutomationProperties.SetName(src, "Source port");
        System.Windows.Automation.AutomationProperties.SetName(dst, "Destination");
        var add = new Button { Content = "Add", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        add.Click += (_, _) =>
        {
            char k = kind.SelectedIndex switch { 1 => 'R', 2 => 'D', _ => 'L' };
            var pf = PuttyImport.ParseForward(k == 'D' ? $"D{src.Text.Trim()}" : $"{k}{src.Text.Trim()}={dst.Text.Trim()}");
            if (pf == null) { FluentDialog.Info(Window.GetWindow(list), "Port forwarding", "Enter a source port and, for Local/Remote, a destination as host:port."); return; }
            get().Add(pf); Changed(); Refresh(); src.Clear(); dst.Clear();
        };
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        remove.Click += (_, _) => { if (list.SelectedItem is PortForward pf) { get().Remove(pf); Changed(); Refresh(); } };
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(kind, Dock.Left); DockPanel.SetDock(src, Dock.Left); DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(kind); row.Children.Add(src); row.Children.Add(remove); row.Children.Add(add); row.Children.Add(dst);
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(row);
        return panel;
    }

    // ------------------------------------------------------------------ environment variables

    public static FrameworkElement Environment(Func<List<EnvVar>> get)
    {
        var list = new ListBox { MaxHeight = 160, MinHeight = 60, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        void Refresh() => list.ItemsSource = get().Select(e => $"{e.Name}={e.Value}").ToList();
        Refresh();
        var name = new TextBox { Width = 150, ToolTip = "Variable" };
        var value = new TextBox { Margin = new Thickness(6, 0, 0, 0), ToolTip = "Value" };
        System.Windows.Automation.AutomationProperties.SetName(name, "Variable");
        System.Windows.Automation.AutomationProperties.SetName(value, "Value");
        var add = new Button { Content = "Add", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        add.Click += (_, _) =>
        {
            if (name.Text.Trim().Length == 0) return;
            get().RemoveAll(e => e.Name == name.Text.Trim());
            get().Add(new EnvVar { Name = name.Text.Trim(), Value = value.Text });
            Changed(); Refresh(); name.Clear(); value.Clear();
        };
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            get().RemoveAt(list.SelectedIndex); Changed(); Refresh();
        };
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(name); row.Children.Add(remove); row.Children.Add(add); row.Children.Add(value);
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(row);
        return panel;
    }

    // ------------------------------------------------------------------ fonts

    private static List<string>? _monoFonts;

    /// <summary>Installed fixed-pitch fonts (same advance for "i" and "M"), computed once.</summary>
    public static List<string> MonospaceFonts()
    {
        if (_monoFonts != null) return _monoFonts;
        var list = new List<string>();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            try
            {
                var tf = new Typeface(f, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!tf.TryGetGlyphTypeface(out var gt)) continue;
                if (!gt.CharacterToGlyphMap.TryGetValue('i', out var gi) || !gt.CharacterToGlyphMap.TryGetValue('M', out var gm)) continue;
                if (Math.Abs(gt.AdvanceWidths[gi] - gt.AdvanceWidths[gm]) < 0.001) list.Add(f.Source);
            }
            catch { }
        }
        _monoFonts = list.Distinct().OrderBy(n => n).ToList();
        return _monoFonts;
    }

    public static FrameworkElement FontPicker(Func<string> get, Action<string> set)
    {
        var box = new ComboBox { IsEditable = true, Width = 240, Text = get() };
        foreach (var f in MonospaceFonts()) box.Items.Add(f);
        box.Text = get();
        void Commit()
        {
            var v = box.Text.Trim();
            if (v.Length > 0 && v != get()) { set(v); Changed(); }
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) { box.Text = s; Commit(); } };
        box.LostKeyboardFocus += (_, _) => Commit();
        return box;
    }

    // ------------------------------------------------------------------ colour schemes

    public static FrameworkElement SchemeEditor()
    {
        var root = new StackPanel();
        var pick = new ComboBox { Width = 260 };
        var dup = SmallButton("Duplicate…", () => { });
        var rename = SmallButton("Rename…", () => { });
        var delete = SmallButton("Delete", () => { });
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(pick); top.Children.Add(new Border { Width = 8 }); top.Children.Add(dup); top.Children.Add(rename); top.Children.Add(delete);
        root.Children.Add(top);
        var note = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        root.Children.Add(note);
        var swatches = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(swatches);
        var preview = new Border { Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10) };
        root.Children.Add(preview);

        ColorScheme? Current() => SettingsStore.AllSchemes.FirstOrDefault(s => s.Name == pick.SelectedItem as string);

        void Fill(string? select = null)
        {
            pick.Items.Clear();
            foreach (var s in SettingsStore.AllSchemes) pick.Items.Add(s.Name);
            pick.SelectedItem = select ?? (ThemeService.IsDark ? SettingsStore.Settings.Appearance.SchemeDark : SettingsStore.Settings.Appearance.SchemeLight);
            if (pick.SelectedIndex < 0) pick.SelectedIndex = 0;
        }

        void Render()
        {
            var s = Current();
            swatches.Children.Clear();
            if (s == null) return;
            bool editable = !s.BuiltIn;
            note.Text = editable ? "Click a colour to change it." : "Built-in schemes can't be changed — duplicate one to make your own.";
            rename.IsEnabled = delete.IsEnabled = editable;
            void Swatch(string label, Func<string?> g, Action<string> st)
            {
                var c = ColorScheme.Parse(g(), Colors.Transparent);
                var sw = new Button { Width = 150, Height = 34, Margin = new Thickness(0, 0, 6, 6), IsEnabled = editable, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2, 6, 2) };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                sw.Content = sp;
                sw.ToolTip = $"{label}: {g()}";
                System.Windows.Automation.AutomationProperties.SetName(sw, label);
                sw.Click += (_, _) =>
                {
                    var hex = ColorDialog(Window.GetWindow(sw), label, g() ?? "#000000");
                    if (hex == null) return;
                    st(hex); Changed(); Render();
                };
                swatches.Children.Add(sw);
            }
            Swatch("Foreground", () => s.Foreground, v => s.Foreground = v);
            Swatch("Background", () => s.Background, v => s.Background = v);
            Swatch("Bold foreground", () => s.BoldForeground ?? s.Foreground, v => s.BoldForeground = v);
            Swatch("Cursor", () => s.Cursor, v => s.Cursor = v);
            Swatch("Cursor text", () => s.CursorText, v => s.CursorText = v);
            Swatch("Selection", () => s.Selection, v => s.Selection = v);
            for (int i = 0; i < 16; i++)
            {
                int idx = i;
                Swatch(ColorScheme.AnsiNames[i], () => s.Ansi[idx], v => s.Ansi[idx] = v);
            }
            RenderPreview(preview, s);
        }

        pick.SelectionChanged += (_, _) => Render();
        dup.Click += (_, _) =>
        {
            var s = Current();
            if (s == null) return;
            var name = FluentDialog.PromptText(Window.GetWindow(root), "New colour scheme", "Name:", s.Name + " (custom)");
            if (string.IsNullOrWhiteSpace(name) || SettingsStore.AllSchemes.Any(x => x.Name == name.Trim())) return;
            SettingsStore.Settings.Appearance.CustomSchemes.Add(s.Clone(name.Trim()));
            Changed(); Fill(name.Trim());
        };
        rename.Click += (_, _) =>
        {
            var s = Current();
            if (s == null || s.BuiltIn) return;
            var name = FluentDialog.PromptText(Window.GetWindow(root), "Rename colour scheme", "Name:", s.Name);
            if (string.IsNullOrWhiteSpace(name) || SettingsStore.AllSchemes.Any(x => x.Name == name.Trim())) return;
            var a = SettingsStore.Settings.Appearance;
            if (a.SchemeDark == s.Name) a.SchemeDark = name.Trim();
            if (a.SchemeLight == s.Name) a.SchemeLight = name.Trim();
            s.Name = name.Trim();
            Changed(); Fill(s.Name);
        };
        delete.Click += (_, _) =>
        {
            var s = Current();
            if (s == null || s.BuiltIn) return;
            if (!FluentDialog.Confirm(Window.GetWindow(root), "Delete colour scheme?", $"Delete \"{s.Name}\"?", "Delete", destructive: true)) return;
            var a = SettingsStore.Settings.Appearance;
            a.CustomSchemes.Remove(s);
            if (a.SchemeDark == s.Name) a.SchemeDark = ColorScheme.DefaultDark;
            if (a.SchemeLight == s.Name) a.SchemeLight = ColorScheme.DefaultLight;
            Changed(); Fill();
        };
        System.Windows.Automation.AutomationProperties.SetName(pick, "Scheme to edit");
        Fill();
        return root;
    }

    /// <summary>A small terminal-looking sample in the scheme's colours.</summary>
    public static void RenderPreview(Border target, ColorScheme s)
    {
        var fg = new SolidColorBrush(ColorScheme.Parse(s.Foreground, Colors.White));
        target.Background = new SolidColorBrush(ColorScheme.Parse(s.Background, Colors.Black));
        var font = new FontFamily("Cascadia Mono, Consolas");
        var sp = new StackPanel();
        TextBlock Line() => new() { FontFamily = font, FontSize = 13, Foreground = fg };
        Run R(string text, int ansi) => new(text) { Foreground = new SolidColorBrush(ColorScheme.Parse(s.Ansi[ansi], Colors.White)) };

        var l1 = Line();
        l1.Inlines.Add(R("admin@core-sw1", 10)); l1.Inlines.Add(new Run(":")); l1.Inlines.Add(R("~", 12)); l1.Inlines.Add(new Run("$ ls -l --color"));
        var l2 = Line();
        l2.Inlines.Add(new Run("drwxr-xr-x 2 admin admin 4096 ")); l2.Inlines.Add(R("configs", 12));
        var l3 = Line();
        l3.Inlines.Add(new Run("-rwxr-xr-x 1 admin admin  812 ")); l3.Inlines.Add(R("backup.sh", 10));
        var l4 = Line();
        l4.Inlines.Add(R("ERROR ", 9)); l4.Inlines.Add(R("WARN ", 11)); l4.Inlines.Add(R("OK ", 2)); l4.Inlines.Add(R("info ", 6)); l4.Inlines.Add(R("debug", 8));
        var l5 = Line();
        for (int i = 0; i < 16; i++)
            l5.Inlines.Add(new Run("  ") { Background = new SolidColorBrush(ColorScheme.Parse(s.Ansi[i], Colors.Black)) });
        var l6 = Line();
        l6.Inlines.Add(new Run("$ ")); l6.Inlines.Add(new Run(" ") { Background = new SolidColorBrush(ColorScheme.Parse(s.Cursor, Colors.White)) });
        foreach (var l in new[] { l1, l2, l3, l4, l5, l6 }) sp.Children.Add(l);
        target.Child = sp;
    }

    /// <summary>Hex entry + RGB sliders. Returns "#RRGGBB" or null.</summary>
    public static string? ColorDialog(Window? owner, string title, string initial)
    {
        var c = ColorScheme.Parse(initial, Colors.Black);
        var panel = new StackPanel();
        var swatch = new Border { Height = 40, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var hex = new TextBox { Text = ColorScheme.ToHex(c), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        System.Windows.Automation.AutomationProperties.SetName(hex, "Hex colour");
        var sliders = new Slider[3];
        bool updating = false;
        void Sync(Color col, bool fromHex)
        {
            updating = true;
            swatch.Background = new SolidColorBrush(col);
            if (!fromHex) hex.Text = ColorScheme.ToHex(col);
            sliders[0].Value = col.R; sliders[1].Value = col.G; sliders[2].Value = col.B;
            updating = false;
        }
        panel.Children.Add(swatch);
        string[] names = { "Red", "Green", "Blue" };
        for (int i = 0; i < 3; i++)
        {
            var sl = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 4, 0, 4) };
            System.Windows.Automation.AutomationProperties.SetName(sl, names[i]);
            sl.ValueChanged += (_, _) =>
            {
                if (updating) return;
                Sync(Color.FromRgb((byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value), false);
            };
            sliders[i] = sl;
            var row = new DockPanel();
            var lbl = new TextBlock { Text = names[i], Width = 50, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lbl, Dock.Left);
            row.Children.Add(lbl);
            row.Children.Add(sl);
            panel.Children.Add(row);
        }
        panel.Children.Add(hex);
        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            var t = hex.Text.Trim();
            if (t.TrimStart('#').Length == 6) Sync(ColorScheme.Parse(t, c), true);
        };
        Sync(c, false);
        var dlg = new FluentDialog(owner, title, panel, "OK") { Width = 380 };
        return dlg.ShowDialog() == DialogResultKind.Primary ? ColorScheme.ToHex(ColorScheme.Parse(hex.Text, c)) : null;
    }
}
