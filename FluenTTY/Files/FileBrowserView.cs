using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Terminal;
using FluenTTY.Views;
using Microsoft.Win32;

namespace FluenTTY.Files;

/// <summary>
/// Remote file browser used by the SFTP side panel of SSH tabs and by SFTP/FTP tabs (MobaXterm-style):
/// navigate, upload (button or drag files in from Explorer), download, rename, delete, chmod, and
/// double-click a file to edit it in FluenTTY's editor (saved back on Ctrl+S).
/// </summary>
public sealed class FileBrowserView : UserControl
{
    private readonly IRemoteFileSystem _fs;
    private readonly bool _compact;
    private readonly ObservableCollection<RemoteEntry> _items = new();
    private readonly ListView _list;
    private readonly TextBox _pathBox;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Border _overlay;
    private readonly TextBlock _overlayText;
    private string _path = "/";
    private string _home = "/";
    private CancellationTokenSource _cts = new();
    private bool _busy;
    private readonly List<FileSystemWatcher> _watchers = new();

    public IRemoteFileSystem FileSystem => _fs;
    public string CurrentPath => _path;

    public FileBrowserView(IRemoteFileSystem fs, bool compact)
    {
        _fs = fs;
        _compact = compact;
        AllowDrop = true;

        // Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
        toolbar.Children.Add(Tool("", "Up one folder (Backspace)", () => _ = Navigate(RemotePath.Parent(_path))));
        toolbar.Children.Add(Tool("", "Refresh (F5)", () => _ = Navigate(_path)));
        toolbar.Children.Add(Tool("", "Home folder", () => _ = Navigate(_home)));
        toolbar.Children.Add(Tool("\uE70F", "Open folder or edit file (Enter)", () => { if (_list?.SelectedItem is RemoteEntry e) Activate(e); }));
        toolbar.Children.Add(Tool("", "New folder", NewFolder));
        toolbar.Children.Add(Tool("", "New file", NewFile));
        toolbar.Children.Add(Tool("", "Upload files…", UploadPick));
        toolbar.Children.Add(Tool("", "Download selected…", DownloadSelected));
        toolbar.Children.Add(Tool("", "Delete selected (Del)", DeleteSelected));

        _pathBox = new TextBox { Margin = new Thickness(6, 2, 6, 6), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = Navigate(_pathBox.Text.Trim()); };
        System.Windows.Automation.AutomationProperties.SetName(_pathBox, "Remote path");

        _list = new ListView
        {
            ItemsSource = _items,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            SelectionMode = SelectionMode.Extended,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.View = BuildColumns();
        // Based on GridView's row style (keeps the columns); only adds an accessible name per row.
        var rowStyle = new Style(typeof(ListViewItem), (Style?)Application.Current.TryFindResource(GridView.GridViewItemContainerStyleKey) ?? (Style?)Application.Current.TryFindResource(typeof(ListViewItem)));
        rowStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(RemoteEntry.Name))));
        _list.ItemContainerStyle = rowStyle;
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListViewItem { DataContext: RemoteEntry entry })
                Activate(entry);
        };
        _list.KeyDown += OnListKey;
        _list.ContextMenuOpening += (_, _) => _list.ContextMenu = BuildMenu();
        _list.ContextMenu = new ContextMenu();

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(StyleProperty, "Caption");
        _progress = new ProgressBar { Height = 3, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), IsIndeterminate = true };
        var statusPanel = new StackPanel { Margin = new Thickness(10, 4, 10, 6) };
        statusPanel.Children.Add(_status);
        statusPanel.Children.Add(_progress);

        _overlayText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16) };
        _overlayText.SetResourceReference(StyleProperty, "Body");
        var retry = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 4, 16, 4) };
        retry.Click += (_, _) => _ = StartAsync();
        var overlayPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        overlayPanel.Children.Add(_overlayText);
        overlayPanel.Children.Add(retry);
        _overlay = new Border { Child = overlayPanel, Visibility = Visibility.Collapsed };

        var header = new TextBlock { Text = fs is SftpFileSystem ? "SFTP" : "FTP", Margin = new Thickness(10, 8, 10, 0), FontWeight = FontWeights.SemiBold };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var dock = new DockPanel();
        var top = new StackPanel();
        if (compact) top.Children.Add(header);
        top.Children.Add(toolbar);
        top.Children.Add(_pathBox);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusPanel, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(statusPanel);
        var center = new Grid();
        center.Children.Add(_list);
        center.Children.Add(_overlay);
        dock.Children.Add(center);

        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 0, compact ? 1 : 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = border;

        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    private Button Tool(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Width = 32, Height = 30 };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.FontSize = 14;
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    private GridView BuildColumns()
    {
        var view = new GridView();
        var nameTemplate = new DataTemplate();
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(RemoteEntry.Glyph)));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(RemoteEntry.Name)));
        name.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        sp.AppendChild(icon);
        sp.AppendChild(name);
        nameTemplate.VisualTree = sp;
        view.Columns.Add(new GridViewColumn { Header = "Name", CellTemplate = nameTemplate, Width = _compact ? 170 : 300 });
        view.Columns.Add(new GridViewColumn { Header = "Size", DisplayMemberBinding = new Binding(nameof(RemoteEntry.SizeText)), Width = 80 });
        if (!_compact)
        {
            view.Columns.Add(new GridViewColumn { Header = "Modified", DisplayMemberBinding = new Binding(nameof(RemoteEntry.ModifiedText)), Width = 140 });
            view.Columns.Add(new GridViewColumn { Header = "Permissions", DisplayMemberBinding = new Binding(nameof(RemoteEntry.Permissions)), Width = 110 });
            view.Columns.Add(new GridViewColumn { Header = "Owner", DisplayMemberBinding = new Binding(nameof(RemoteEntry.Owner)), Width = 100 });
        }
        else
        {
            view.Columns.Add(new GridViewColumn { Header = "Modified", DisplayMemberBinding = new Binding(nameof(RemoteEntry.ModifiedText)), Width = 120 });
        }
        return view;
    }

    // ================================================================== connect / navigate

    public async Task StartAsync()
    {
        _overlay.Visibility = Visibility.Collapsed;
        SetBusy("Connecting…");
        try
        {
            if (!_fs.IsConnected) await _fs.ConnectAsync(_cts.Token);
            _home = await _fs.GetHomeAsync(_cts.Token);
            if (string.IsNullOrEmpty(_home)) _home = "/";
            await Navigate(_pendingFollow ?? _home);
        }
        catch (OperationCanceledException)
        {
            ShowError("Connection cancelled.");
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't connect: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ClearBusy(message);
        _overlayText.Text = message;
        _overlay.Visibility = Visibility.Visible;
    }

    private string? _pendingFollow;

    /// <summary>Follows the terminal's working directory (OSC 7 from the shell).</summary>
    public void FollowDirectory(string dir)
    {
        if (!_fs.IsConnected) { _pendingFollow = dir; return; }
        if (dir != _path && !_busy) _ = Navigate(dir);
    }

    public async Task Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = "/";
        SetBusy($"Listing {path}");
        try
        {
            var entries = await _fs.ListAsync(path, _cts.Token);
            bool hidden = SettingsStore.Settings.Files.ShowHiddenFiles;
            var sorted = entries
                .Where(e => e.Name != "." && e.Name != ".." && (hidden || !e.Name.StartsWith('.')))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _items.Clear();
            if (path != "/") _items.Add(new RemoteEntry { Name = "..", FullPath = RemotePath.Parent(path), IsDirectory = true });
            foreach (var e in sorted) _items.Add(e);
            _path = path;
            _pathBox.Text = path;
            _overlay.Visibility = Visibility.Collapsed;
            ClearBusy($"{sorted.Count} items");
        }
        catch (OperationCanceledException) { ClearBusy(""); }
        catch (Exception ex)
        {
            ClearBusy($"Couldn't open {path}: {ex.Message}");
            if (!_fs.IsConnected) ShowError($"Disconnected: {ex.Message}");
        }
    }

    private void SetBusy(string text)
    {
        _busy = true;
        _status.Text = text;
        _progress.IsIndeterminate = true;
        _progress.Visibility = Visibility.Visible;
    }

    private void ClearBusy(string text)
    {
        _busy = false;
        _status.Text = text;
        _progress.Visibility = Visibility.Collapsed;
    }

    private IEnumerable<RemoteEntry> Selected => _list.SelectedItems.Cast<RemoteEntry>().Where(e => !e.IsParentLink).ToList();

    private void OnListKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _list.SelectedItem is RemoteEntry entry: Activate(entry); e.Handled = true; break;
            case Key.Back: _ = Navigate(RemotePath.Parent(_path)); e.Handled = true; break;
            case Key.Delete: DeleteSelected(); e.Handled = true; break;
            case Key.F2 when _list.SelectedItem is RemoteEntry r: Rename(r); e.Handled = true; break;
            case Key.F5: _ = Navigate(_path); e.Handled = true; break;
        }
    }

    private void Activate(RemoteEntry entry)
    {
        if (entry.IsDirectory) { _ = Navigate(entry.FullPath); return; }
        switch (SettingsStore.Settings.Files.DoubleClick)
        {
            case FileDoubleClick.Download: _ = DownloadTo(new[] { entry }, null); break;
            case FileDoubleClick.OpenWithDefaultApp: _ = OpenExternally(entry); break;
            default: _ = Edit(entry); break;
        }
    }

    // ================================================================== editing

    public async Task Edit(RemoteEntry entry)
    {
        var max = SettingsStore.Settings.Files.MaxEditSizeMB * 1024L * 1024L;
        if (entry.Size > max && !FluentDialog.Confirm(Window.GetWindow(this), "Large file",
                $"{entry.Name} is {RemoteEntry.FormatSize(entry.Size)}. Open it in the editor anyway?", "Open"))
            return;
        SetBusy($"Opening {entry.Name}");
        try
        {
            var ms = new MemoryStream();
            await _fs.DownloadAsync(entry.FullPath, ms, null, _cts.Token);
            ClearBusy($"Opened {entry.Name}");
            var editor = new EditorTab(_fs, entry.FullPath, ms.ToArray(), entry.Mode);
            if (SettingsStore.Settings.Editor.OpenIn == EditorPlacement.Window) PopOutWindow.Open(editor);
            else TabService.Open(editor);
        }
        catch (Exception ex)
        {
            ClearBusy($"Couldn't open {entry.Name}: {ex.Message}");
        }
    }

    /// <summary>Where downloaded files came from, for Mark-of-the-Web (no user name in it).</summary>
    private string SourceUrl(RemoteEntry e) =>
        System.Text.RegularExpressions.Regex.Replace(_fs.DisplayName, "//[^/@]+@", "//").TrimEnd('/') + e.FullPath;

    /// <summary>Downloads to a temp file, opens it with the Windows default app and uploads it again whenever it's saved.</summary>
    private async Task OpenExternally(RemoteEntry entry)
    {
        var editor = SettingsStore.Settings.Editor.ExternalEditor;
        var safeName = LocalPaths.SafeFileName(entry.Name);
        // The server picks the extension, and the extension picks what Windows does with the file: a
        // .bat, .lnk, .hta or .msi would run. Security review M1 — see CLAUDE.md "Security invariants".
        if (string.IsNullOrWhiteSpace(editor) && LocalPaths.IsRunnable(safeName))
        {
            var choice = new FluentDialog(Window.GetWindow(this), "This file can run code",
                $"\"{safeName}\" is a program, script, shortcut or installer ({Path.GetExtension(safeName)}). Opening it with its " +
                "default app would run it on this PC with your permissions — only do that if you trust the server and the file.",
                "Open in the editor", "Run it anyway", "Cancel", primaryIsAccent: true).ShowDialog();
            if (choice == DialogResultKind.Primary) { _ = Edit(entry); return; }
            if (choice != DialogResultKind.Secondary) return;
        }
        var dir = Path.Combine(AppInfo.TempDirectory, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string local;
        try { local = LocalPaths.ChildPath(dir, entry.Name); }
        catch (IOException ex) { ClearBusy(ex.Message); return; }
        SetBusy($"Downloading {safeName}");
        try
        {
            await using (var fsOut = File.Create(local)) await _fs.DownloadAsync(entry.FullPath, fsOut, null, _cts.Token);
            LocalPaths.MarkFromInternet(local, SourceUrl(entry));
            ClearBusy($"Opened {safeName} — changes you save are uploaded automatically");
            var psi = string.IsNullOrWhiteSpace(editor)
                ? new ProcessStartInfo(local) { UseShellExecute = true }
                : new ProcessStartInfo(Environment.ExpandEnvironmentVariables(editor), $"\"{local}\"") { UseShellExecute = true };
            Process.Start(psi);
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(local)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            DateTime last = File.GetLastWriteTimeUtc(local);
            watcher.Changed += (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(400);
                var now = File.GetLastWriteTimeUtc(local);
                if (now == last) return;
                last = now;
                try
                {
                    byte[] data = await ReadShared(local);
                    await _fs.UploadAsync(new MemoryStream(data), entry.FullPath, null, _cts.Token);
                    _status.Text = $"Uploaded {entry.Name} ({DateTime.Now:HH:mm:ss})";
                }
                catch (Exception ex) { _status.Text = $"Upload of {entry.Name} failed: {ex.Message}"; }
            });
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            ClearBusy($"Couldn't open {entry.Name}: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch (IOException) when (i < 10) { await Task.Delay(200); }
        }
    }

    // ================================================================== transfers

    private void UploadPick()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = $"Upload to {_path}" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = Upload(dlg.FileNames);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Upload(files);
    }

    public async Task Upload(IEnumerable<string> localPaths)
    {
        var settings = SettingsStore.Settings.Files;
        foreach (var local in localPaths)
        {
            try
            {
                if (Directory.Exists(local)) { await UploadDirectory(local, RemotePath.Combine(_path, Path.GetFileName(local))); continue; }
                var target = RemotePath.Combine(_path, Path.GetFileName(local));
                if (settings.ConfirmOverwrite && _items.Any(i => i.Name == Path.GetFileName(local)) &&
                    !FluentDialog.Confirm(Window.GetWindow(this), "Replace file?", $"{Path.GetFileName(local)} already exists in {_path}. Replace it?", "Replace"))
                    continue;
                await UploadFile(local, target);
            }
            catch (Exception ex)
            {
                _status.Text = $"Upload of {Path.GetFileName(local)} failed: {ex.Message}";
                _progress.Visibility = Visibility.Collapsed;
            }
        }
        await Navigate(_path);
    }

    private async Task UploadFile(string local, string remote)
    {
        var info = new FileInfo(local);
        _progress.IsIndeterminate = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Maximum = Math.Max(1, info.Length);
        _status.Text = $"Uploading {info.Name}";
        var progress = new Progress<long>(v => { _progress.Value = v; _status.Text = $"Uploading {info.Name} — {RemoteEntry.FormatSize(v)} of {RemoteEntry.FormatSize(info.Length)}"; });
        await using var fs = File.OpenRead(local);
        await _fs.UploadAsync(fs, remote, progress, _cts.Token);
        _progress.Visibility = Visibility.Collapsed;
        _status.Text = $"Uploaded {info.Name}";
    }

    private async Task UploadDirectory(string local, string remote)
    {
        try { await _fs.CreateDirectoryAsync(remote, _cts.Token); } catch { }
        foreach (var f in Directory.GetFiles(local)) await UploadFile(f, RemotePath.Combine(remote, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(local)) await UploadDirectory(d, RemotePath.Combine(remote, Path.GetFileName(d)));
    }

    private void DownloadSelected()
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) return;
        _ = DownloadTo(sel, null);
    }

    private async Task DownloadTo(IList<RemoteEntry> entries, string? folder)
    {
        folder ??= PickFolder();
        if (folder == null) return;
        // Server-supplied names never become paths directly: LocalPaths keeps every file inside `folder`
        // and bounds folder recursion. Security review H1 — see CLAUDE.md "Security invariants".
        var skipped = new List<string>();
        foreach (var e in entries)
        {
            try
            {
                await LocalPaths.DownloadTree(_fs, e, folder, DownloadFile, skipped, _cts.Token);
            }
            catch (Exception ex)
            {
                _status.Text = $"Download of {LocalPaths.SafeFileName(e.Name)} failed: {ex.Message}";
                _progress.Visibility = Visibility.Collapsed;
            }
        }
        if (skipped.Count > 0)
        {
            var list = string.Join("\n", skipped.Take(12)) + (skipped.Count > 12 ? $"\n… and {skipped.Count - 12} more" : "");
            FluentDialog.Info(Window.GetWindow(this), "Some items weren't downloaded", list);
        }
    }

    private string? PickFolder()
    {
        var configured = SettingsStore.Settings.Files.DownloadFolder;
        var dlg = new OpenFolderDialog
        {
            Title = "Download to",
            InitialDirectory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : Environment.ExpandEnvironmentVariables(configured),
        };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FolderName : null;
    }

    private async Task DownloadFile(RemoteEntry e, string local)
    {
        if (File.Exists(local) && SettingsStore.Settings.Files.ConfirmOverwrite &&
            !FluentDialog.Confirm(Window.GetWindow(this), "Replace file?", $"{local} already exists. Replace it?", "Replace"))
            return;
        _progress.IsIndeterminate = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Maximum = Math.Max(1, e.Size);
        var progress = new Progress<long>(v => { _progress.Value = v; _status.Text = $"Downloading {e.Name} — {RemoteEntry.FormatSize(v)} of {RemoteEntry.FormatSize(e.Size)}"; });
        await using (var fs = File.Create(local)) await _fs.DownloadAsync(e.FullPath, fs, progress, _cts.Token);
        LocalPaths.MarkFromInternet(local, SourceUrl(e));
        if (SettingsStore.Settings.Files.PreserveTimestamps && e.Modified.HasValue)
            try { File.SetLastWriteTime(local, e.Modified.Value); } catch { }
        _progress.Visibility = Visibility.Collapsed;
        _status.Text = $"Downloaded {Path.GetFileName(local)} to {Path.GetDirectoryName(local)}";
    }

    // ================================================================== file operations

    private void NewFolder()
    {
        var name = FluentDialog.PromptText(Window.GetWindow(this), "New folder", $"Folder name in {_path}:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _ = Do(() => _fs.CreateDirectoryAsync(RemotePath.Combine(_path, name.Trim()), _cts.Token), $"Created {name}");
    }

    private void NewFile()
    {
        var name = FluentDialog.PromptText(Window.GetWindow(this), "New file", $"File name in {_path}:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = RemotePath.Combine(_path, name.Trim());
        _ = Do(async () =>
        {
            await _fs.UploadAsync(new MemoryStream(), path, null, _cts.Token);
            await Edit(new RemoteEntry { Name = name.Trim(), FullPath = path });
        }, $"Created {name}");
    }

    private void Rename(RemoteEntry entry)
    {
        var name = FluentDialog.PromptText(Window.GetWindow(this), "Rename", $"New name for {entry.Name}:", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        _ = Do(() => _fs.RenameAsync(entry.FullPath, RemotePath.Combine(RemotePath.Parent(entry.FullPath), name.Trim()), _cts.Token), $"Renamed to {name}");
    }

    private void DeleteSelected()
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) return;
        if (SettingsStore.Settings.Files.ConfirmDelete)
        {
            string what = sel.Count == 1 ? $"\"{sel[0].Name}\"{(sel[0].IsDirectory ? " and everything in it" : "")}" : $"{sel.Count} items";
            if (!FluentDialog.Confirm(Window.GetWindow(this), "Delete permanently?", $"Delete {what} from the server? This can't be undone.", "Delete", destructive: true))
                return;
        }
        _ = Do(async () => { foreach (var e in sel) await _fs.DeleteAsync(e, _cts.Token); }, $"Deleted {sel.Count} item(s)");
    }

    private void ChangePermissions(RemoteEntry entry)
    {
        var current = entry.Mode.HasValue ? Convert.ToString(entry.Mode.Value, 8) : "644";
        var text = FluentDialog.PromptText(Window.GetWindow(this), "Permissions", $"Octal mode for {entry.Name} (e.g. 644, 755):", current);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            int mode = Convert.ToInt32(text.Trim(), 8);
            _ = Do(() => _fs.SetPermissionsAsync(entry.FullPath, mode, _cts.Token), $"Permissions of {entry.Name} set to {text.Trim()}");
        }
        catch (FormatException)
        {
            FluentDialog.Info(Window.GetWindow(this), "Permissions", "Enter the mode as octal digits, like 644 or 755.");
        }
    }

    private async Task Do(Func<Task> action, string done)
    {
        SetBusy("Working…");
        try
        {
            await action();
            await Navigate(_path);
            _status.Text = done;
        }
        catch (Exception ex)
        {
            ClearBusy($"Failed: {ex.Message}");
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var entry = _list.SelectedItem as RemoteEntry;
        MenuItem Mi(string header, string glyph, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };
            mi.Click += (_, _) => a();
            return mi;
        }
        bool file = entry != null && !entry.IsDirectory;
        bool real = entry != null && !entry.IsParentLink;
        menu.Items.Add(Mi("Edit", "", () => _ = Edit(entry!), file));
        menu.Items.Add(Mi("Open with default app", "", () => _ = OpenExternally(entry!), file));
        menu.Items.Add(Mi("Download…", "", DownloadSelected, real));
        menu.Items.Add(Mi("Upload here…", "", UploadPick));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("Rename", "", () => Rename(entry!), real));
        menu.Items.Add(Mi("Permissions…", "", () => ChangePermissions(entry!), real && _fs.SupportsPermissions));
        menu.Items.Add(Mi("Delete", "", DeleteSelected, real));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("New folder", "", NewFolder));
        menu.Items.Add(Mi("New file", "", NewFile));
        menu.Items.Add(Mi("Copy path", "", () => TerminalCanvas.TrySetClipboard(entry?.FullPath ?? _path)));
        menu.Items.Add(Mi("Refresh", "", () => _ = Navigate(_path)));
        return menu;
    }

    public void ApplySettings() => _ = Navigate(_path);

    public void Shutdown()
    {
        _cts.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        Task.Run(() => { try { _fs.Dispose(); } catch { } });
    }
}
