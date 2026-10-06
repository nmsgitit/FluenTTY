using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>
/// Saved sessions in folders (MobaXterm/PuTTY CM-style tree): double-click to connect, drag sessions
/// between folders, right-click for edit/duplicate/delete/move, search box filters as you type.
/// </summary>
public sealed class SessionsSidebar : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBox _search;
    private readonly TextBlock _empty;
    private readonly HashSet<string> _expanded = new() { "" };
    private static readonly HashSet<string> Collapsed = new();

    public event Action<SessionProfile, bool>? ConnectRequested;

    public SessionsSidebar()
    {
        var header = new Grid { Margin = new Thickness(8, 4, 2, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "Sessions", VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(StyleProperty, "BodyStrong");
        header.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Small("", "New session", () => NewSession("")));
        buttons.Children.Add(Small("", "New folder", () => NewFolder("")));
        buttons.Children.Add(Small("", "Import sessions", ShowImportMenu));
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        _search = new TextBox { Margin = new Thickness(6, 0, 6, 6) };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Filter sessions");
        _search.TextChanged += (_, _) => Rebuild();
        var hint = new TextBlock { Text = "Filter", Margin = new Thickness(18, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        searchGrid.Children.Add(hint);

        // No horizontal scrolling: top-level folders always stay anchored at the left edge and long names
        // deeper in the tree are trimmed with "…" instead (a horizontal offset hid the parents and was
        // re-applied whenever selection brought a wide row into view). The right padding keeps the folder
        // counts clear of the overlay vertical scrollbar.
        _tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(2, 0, 12, 0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "SessionTreeItem");
        _tree.MouseDoubleClick += (_, e) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: SessionProfile p } && e.OriginalSource is DependencyObject d && FindItem(d)?.Tag is SessionProfile)
            {
                ConnectRequested?.Invoke(p, false);
                e.Handled = true;
            }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: SessionProfile p })
            {
                if (e.Key == Key.Enter) { ConnectRequested?.Invoke(p, Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
                else if (e.Key == Key.Delete) { Delete(p); e.Handled = true; }
                else if (e.Key == Key.F2) { Edit(p); e.Handled = true; }
            }
        };
        _tree.AllowDrop = true;

        _empty = new TextBlock
        {
            Text = "No saved sessions yet.\nUse + to add one, or import them from PuTTY or PuTTY Connection Manager.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), TextAlignment = TextAlignment.Center,
        };
        _empty.SetResourceReference(StyleProperty, "Caption");

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(searchGrid, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(searchGrid);
        var body = new Grid();
        body.Children.Add(_tree);
        body.Children.Add(_empty);
        dock.Children.Add(body);
        Content = dock;

        SettingsStore.SessionsChanged += () => Dispatcher.BeginInvoke(Rebuild);
        Loaded += (_, _) => Rebuild();
    }

    public void FocusSearch() => Dispatcher.BeginInvoke(() => _search.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private Button Small(string glyph, string tip, Action a)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "SmallIconButton");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private static TreeViewItem? FindItem(DependencyObject d)
    {
        while (d != null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    // ================================================================== tree

    private sealed class FolderNode
    {
        public string Path = "";
        public string Name = "";
        public SortedDictionary<string, FolderNode> Folders = new(StringComparer.OrdinalIgnoreCase);
        public List<SessionProfile> Sessions = new();
    }

    private void Rebuild()
    {
        var filter = _search.Text.Trim();
        var root = new FolderNode();
        var data = SettingsStore.Sessions;
        IEnumerable<SessionProfile> sessions = data.Sessions;
        if (filter.Length > 0)
            sessions = sessions.Where(s => $"{s.Name} {s.Host} {s.Username} {s.Folder} {s.Notes}".Contains(filter, StringComparison.OrdinalIgnoreCase));

        FolderNode Ensure(string path)
        {
            var node = root;
            if (string.IsNullOrWhiteSpace(path)) return node;
            string acc = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc.Length == 0 ? part : acc + "/" + part;
                if (!node.Folders.TryGetValue(part, out var next))
                {
                    next = new FolderNode { Name = part, Path = acc };
                    node.Folders[part] = next;
                }
                node = next;
            }
            return node;
        }

        if (filter.Length == 0) foreach (var f in data.Folders) Ensure(f);
        foreach (var s in sessions) Ensure(s.Folder).Sessions.Add(s);

        _tree.Items.Clear();
        AddChildren(_tree.Items, root, filter.Length > 0);
        _empty.Visibility = data.Sessions.Count == 0 && data.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddChildren(ItemCollection items, FolderNode node, bool expandAll)
    {
        foreach (var f in node.Folders.Values)
        {
            var item = new TreeViewItem { Tag = f.Path, Header = HeaderContent("", f.Name, $"{Count(f)}"), IsExpanded = expandAll || !Collapsed.Contains(f.Path) };
            item.SetResourceReference(StyleProperty, "SessionTreeItem");
            item.Expanded += (_, e) => { Collapsed.Remove(f.Path); e.Handled = true; };
            item.Collapsed += (_, e) => { Collapsed.Add(f.Path); e.Handled = true; };
            item.ContextMenu = FolderMenu(f.Path);
            item.AllowDrop = true;
            item.Drop += (_, e) => OnDropOnFolder(e, f.Path);
            System.Windows.Automation.AutomationProperties.SetName(item, f.Name);
            AddChildren(item.Items, f, expandAll);
            items.Add(item);
        }
        foreach (var s in node.Sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TreeViewItem
            {
                Tag = s,
                Header = HeaderContent(SessionProfile.ProtocolGlyph(s.Protocol), string.IsNullOrEmpty(s.Name) ? s.DisplayTarget : s.Name, null),
                ToolTip = $"{SessionProfile.ProtocolName(s.Protocol)} · {s.DisplayTarget}{(string.IsNullOrWhiteSpace(s.Notes) ? "" : "\n" + s.Notes)}",
            };
            item.SetResourceReference(StyleProperty, "SessionTreeItem");
            item.ContextMenu = SessionMenu(s);
            item.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && item.IsSelected)
                    DragDrop.DoDragDrop(item, new DataObject("FluenTTY.Session", s.Id.ToString()), DragDropEffects.Move);
            };
            System.Windows.Automation.AutomationProperties.SetName(item, s.Name);
            items.Add(item);
        }
    }

    private static int Count(FolderNode f) => f.Sessions.Count + f.Folders.Values.Sum(Count);

    private static FrameworkElement HeaderContent(string glyph, string text, string? badge)
    {
        var sp = new DockPanel();
        var icon = new TextBlock { Text = glyph, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, badge != null ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush");
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(StyleProperty, "Body");
        label.FontSize = 13;
        DockPanel.SetDock(icon, Dock.Left);
        sp.Children.Add(icon);
        if (badge != null)
        {
            var b = new TextBlock { Text = badge, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(StyleProperty, "Caption");
            DockPanel.SetDock(b, Dock.Right);
            sp.Children.Add(b);
        }
        sp.Children.Add(label);
        return sp;
    }

    private void OnDropOnFolder(DragEventArgs e, string folder)
    {
        e.Handled = true;
        if (e.Data.GetData("FluenTTY.Session") is not string id) return;
        var s = SettingsStore.Sessions.Sessions.FirstOrDefault(x => x.Id.ToString() == id);
        if (s == null || s.Folder == folder) return;
        s.Folder = folder;
        SettingsStore.SaveSessions();
    }

    // ================================================================== menus / actions

    private static MenuItem Mi(string header, string glyph, Action a)
    {
        var mi = new MenuItem { Header = header };
        mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 14 };
        mi.Click += (_, _) => a();
        return mi;
    }

    private ContextMenu SessionMenu(SessionProfile s)
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("Connect", "", () => ConnectRequested?.Invoke(s, false)));
        m.Items.Add(Mi("Connect in a new window", "", () => ConnectRequested?.Invoke(s, true)));
        if (s.Protocol == ProtocolKind.Ssh)
            m.Items.Add(Mi("Open SFTP browser", "", () => { var c = s.Clone(); c.Protocol = ProtocolKind.Sftp; ConnectRequested?.Invoke(c, false); }));
        m.Items.Add(new Separator());
        m.Items.Add(Mi("Edit…", "", () => Edit(s)));
        m.Items.Add(Mi("Duplicate", "", () =>
        {
            var c = s.Clone();
            c.Id = Guid.NewGuid();
            c.Name = s.Name + " (copy)";
            SettingsStore.AddOrUpdateSession(c);
        }));
        m.Items.Add(Mi("Rename…", "", () =>
        {
            var name = FluentDialog.PromptText(Window.GetWindow(this), "Rename session", "Name:", s.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            s.Name = name.Trim();
            SettingsStore.SaveSessions();
        }));
        m.Items.Add(Mi("Move to folder…", "", () =>
        {
            var folder = FluentDialog.PromptText(Window.GetWindow(this), "Move to folder", "Folder path (use / for sub-folders, leave empty for top level):", s.Folder);
            if (folder == null) return;
            s.Folder = folder.Trim().Trim('/');
            SettingsStore.SaveSessions();
        }));
        m.Items.Add(Mi("Delete", "", () => Delete(s)));
        return m;
    }

    private ContextMenu FolderMenu(string path)
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("New session here…", "", () => NewSession(path)));
        m.Items.Add(Mi("New sub-folder…", "", () => NewFolder(path)));
        m.Items.Add(Mi("Connect to all", "", () =>
        {
            var all = SettingsStore.Sessions.Sessions.Where(s => s.Folder == path || s.Folder.StartsWith(path + "/")).ToList();
            if (all.Count > 10 && !FluentDialog.Confirm(Window.GetWindow(this), "Open many sessions?", $"Open {all.Count} sessions at once?", "Open all")) return;
            foreach (var s in all) ConnectRequested?.Invoke(s, false);
        }));
        m.Items.Add(Mi("Rename folder…", "", () =>
        {
            var leaf = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            var name = FluentDialog.PromptText(Window.GetWindow(this), "Rename folder", "Folder name:", leaf);
            if (string.IsNullOrWhiteSpace(name)) return;
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var newPath = (parent.Length > 0 ? parent + "/" : "") + name.Trim().Replace('/', '-');
            foreach (var s in SettingsStore.Sessions.Sessions)
            {
                if (s.Folder == path) s.Folder = newPath;
                else if (s.Folder.StartsWith(path + "/")) s.Folder = newPath + s.Folder[path.Length..];
            }
            var folders = SettingsStore.Sessions.Folders;
            for (int i = 0; i < folders.Count; i++)
            {
                if (folders[i] == path) folders[i] = newPath;
                else if (folders[i].StartsWith(path + "/")) folders[i] = newPath + folders[i][path.Length..];
            }
            SettingsStore.SaveSessions();
        }));
        m.Items.Add(Mi("Delete folder…", "", () =>
        {
            var inside = SettingsStore.Sessions.Sessions.Where(s => s.Folder == path || s.Folder.StartsWith(path + "/")).ToList();
            if (!FluentDialog.Confirm(Window.GetWindow(this), "Delete folder?",
                    inside.Count == 0 ? $"Delete the folder \"{path}\"?" : $"Delete \"{path}\" and the {inside.Count} session(s) in it?", "Delete", destructive: true))
                return;
            SettingsStore.Sessions.Sessions.RemoveAll(s => inside.Contains(s));
            SettingsStore.Sessions.Folders.RemoveAll(f => f == path || f.StartsWith(path + "/"));
            SettingsStore.SaveSessions();
        }));
        return m;
    }

    private void ShowImportMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(Mi("Import PuTTY sessions", "", () =>
        {
            int n = PuttyImport.ImportSessions(overwrite: false);
            FluentDialog.Info(Window.GetWindow(this), "Import from PuTTY", n == 0 ? "No new PuTTY sessions were found." : $"Imported {n} session(s) into the \"PuTTY\" folder.");
        }));
        m.Items.Add(Mi("Import PuTTY Connection Manager database…", "", () => ImportWindow.ImportPcm(Window.GetWindow(this))));
        m.Items.Add(Mi("Import / export settings…", "", () => SettingsWindow.ShowSingle(Window.GetWindow(this), "backup")));
        m.PlacementTarget = this;
        m.IsOpen = true;
    }

    private void NewSession(string folder)
    {
        var p = new SessionProfile { Folder = folder };
        var result = SessionEditorWindow.Edit(Window.GetWindow(this), p, isNew: true);
        if (result.Profile == null) return;
        if (result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
        if (result.Connect) ConnectRequested?.Invoke(result.Profile, false);
    }

    private void NewFolder(string parent)
    {
        var name = FluentDialog.PromptText(Window.GetWindow(this), "New folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = (parent.Length > 0 ? parent + "/" : "") + name.Trim().Replace('/', '-');
        if (!SettingsStore.Sessions.Folders.Contains(path)) SettingsStore.Sessions.Folders.Add(path);
        SettingsStore.SaveSessions();
    }

    private void Edit(SessionProfile s)
    {
        var result = SessionEditorWindow.Edit(Window.GetWindow(this), s.Clone(), isNew: false);
        if (result.Profile == null) return;
        if (result.Save) SettingsStore.AddOrUpdateSession(result.Profile);
        if (result.Connect) ConnectRequested?.Invoke(result.Profile, false);
    }

    private void Delete(SessionProfile s)
    {
        if (!FluentDialog.Confirm(Window.GetWindow(this), "Delete session?", $"Delete the saved session \"{s.Name}\"?", "Delete", destructive: true)) return;
        SettingsStore.DeleteSession(s.Id);
    }
}
