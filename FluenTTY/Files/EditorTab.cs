using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Services;
using FluenTTY.Views;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;

namespace FluenTTY.Files;

/// <summary>
/// Embedded text editor (AvalonEdit) for a remote file: syntax highlighting by extension, find/replace,
/// go to line, and Ctrl+S to upload the file back over the same SFTP/FTP connection.
/// Keeps the file's encoding (BOM or not) and line endings.
/// </summary>
public sealed class EditorTab : SessionTab
{
    private readonly IRemoteFileSystem _fs;
    private readonly string _remotePath;
    private readonly TextEditor _editor;
    private readonly FindBar _findBar;
    private readonly TextBlock _statusLeft, _statusRight;
    private Encoding _encoding;
    private bool _dirty;
    private bool _saving;

    public EditorTab(IRemoteFileSystem fs, string remotePath, byte[] content, int? mode)
    {
        _fs = fs;
        _remotePath = remotePath;
        Glyph = "";
        Title = RemotePath.FileName(remotePath);

        _encoding = DetectEncoding(content, out int bomLength);
        var text = _encoding.GetString(content, bomLength, content.Length - bomLength);

        _editor = new TextEditor
        {
            Text = text,
            ShowLineNumbers = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 0, 0),
        };
        _findBar = new FindBar(_editor);
        System.Windows.Automation.AutomationProperties.SetName(_editor, $"Editor {RemotePath.FileName(remotePath)}");
        // Breathing room between the line numbers and the text.
        _editor.TextArea.LeftMargins.Add(new Border { Width = 10 });
        _editor.TextChanged += (_, _) => SetDirty(true);
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        _editor.Document.UndoStack.ClearAll();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 4) };
        toolbar.Children.Add(ToolButton("Save", "", "Upload to the server (Ctrl+S)", () => _ = SaveAsync()));
        toolbar.Children.Add(ToolButton("Reload", "", "Discard changes and download again", () => _ = ReloadAsync()));
        toolbar.Children.Add(ToolButton("Save a copy", "", "Save a local copy", SaveLocalCopy));
        toolbar.Children.Add(ToolButton("Find", "", "Find (Ctrl+F) and replace (Ctrl+H)", () => _findBar.Open(replace: false)));
        var wrap = new CheckBox { Content = "Word wrap", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        wrap.IsChecked = SettingsStore.Settings.Editor.WordWrap;
        wrap.Checked += (_, _) => _editor.WordWrap = true;
        wrap.Unchecked += (_, _) => _editor.WordWrap = false;
        toolbar.Children.Add(wrap);

        var path = new TextBlock { Text = $"{fs.DisplayName}  {remotePath}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        path.SetResourceReference(StyleProperty, "Caption");
        toolbar.Children.Add(path);

        _statusLeft = new TextBlock();
        _statusLeft.SetResourceReference(StyleProperty, "Caption");
        _statusRight = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        _statusRight.SetResourceReference(StyleProperty, "Caption");
        var status = new Grid { Margin = new Thickness(10, 4, 10, 6) };
        status.Children.Add(_statusLeft);
        status.Children.Add(_statusRight);

        var editorBorder = new Border { Child = _editor, BorderThickness = new Thickness(0, 1, 0, 1) };
        editorBorder.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(toolbar);
        dock.Children.Add(_findBar);
        dock.Children.Add(status);
        dock.Children.Add(editorBorder);
        var root = new Border { Child = dock };
        root.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        Content = root;

        InputBindings.Add(new KeyBinding(new RelayCommand(() => _ = SaveAsync()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(GoToLine), Key.G, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _findBar.Open(replace: false)), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _findBar.Open(replace: true)), Key.H, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _findBar.Go(true)), Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _findBar.Go(false)), Key.F3, ModifierKeys.Shift));
        _editor.TextArea.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _findBar.IsOpen) { _findBar.Close(); e.Handled = true; } };

        ApplySettings();
        SetDirty(false);
        UpdateStatus();
        State = TabState.Connected;
        Loaded += (_, _) => _editor.Focus();
    }

    public override string Description => $"{_fs.DisplayName}{_remotePath}";
    public override bool IsLive => false;
    public override bool CanDuplicate => false;

    private Button ToolButton(string text, string glyph, string tip, Action action)
    {
        var b = new Button { Content = text, Tag = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "ToolbarButton");
        b.Click += (_, _) => action();
        return b;
    }

    private static Encoding DetectEncoding(byte[] data, out int bom)
    {
        bom = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { bom = 3; return new UTF8Encoding(true); }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { bom = 2; return new UnicodeEncoding(false, true); }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { bom = 2; return new UnicodeEncoding(true, true); }
        try
        {
            new UTF8Encoding(false, true).GetString(data);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Terminal.TerminalConfig.GetEncoding("Windows-1252");
        }
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        Title = (dirty ? "● " : "") + RemotePath.FileName(_remotePath);
    }

    private void UpdateStatus()
    {
        var caret = _editor.TextArea.Caret;
        string eol = _editor.Text.Contains("\r\n") ? "CRLF" : _editor.Text.Contains('\n') ? "LF" : "—";
        string enc = _encoding is UTF8Encoding u ? (u.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8") : _encoding.WebName;
        _statusRight.Text = $"Ln {caret.Line}, Col {caret.Column}    {enc}    {eol}    {_editor.SyntaxHighlighting?.Name ?? "Plain text"}";
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        _statusLeft.Text = "Uploading…";
        try
        {
            var body = _encoding.GetBytes(_editor.Text);
            var pre = _encoding.GetPreamble();
            var data = new byte[pre.Length + body.Length];
            pre.CopyTo(data, 0);
            body.CopyTo(data, pre.Length);
            if (!_fs.IsConnected) await _fs.ConnectAsync(CancellationToken.None);
            await _fs.UploadAsync(new MemoryStream(data), _remotePath, null, CancellationToken.None);
            SetDirty(false);
            _statusLeft.Text = $"Saved to the server at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _statusLeft.Text = $"Save failed: {ex.Message}";
            FluentDialog.Info(Window.GetWindow(this), "Couldn't save", $"The file couldn't be uploaded:\n{ex.Message}\n\nYour changes are still in the editor. Use \"Save a copy\" to keep them locally.");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task ReloadAsync()
    {
        if (_dirty && !FluentDialog.Confirm(Window.GetWindow(this), "Discard changes?", "Reload the file from the server and lose your edits?", "Reload", destructive: true))
            return;
        try
        {
            var ms = new MemoryStream();
            await _fs.DownloadAsync(_remotePath, ms, null, CancellationToken.None);
            var data = ms.ToArray();
            _encoding = DetectEncoding(data, out int bom);
            _editor.Text = _encoding.GetString(data, bom, data.Length - bom);
            SetDirty(false);
            _statusLeft.Text = "Reloaded";
        }
        catch (Exception ex)
        {
            _statusLeft.Text = $"Reload failed: {ex.Message}";
        }
    }

    private void SaveLocalCopy()
    {
        var dlg = new SaveFileDialog { FileName = LocalPaths.SafeFileName(RemotePath.FileName(_remotePath)), Title = "Save a local copy" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, _editor.Text, _encoding);
        _statusLeft.Text = $"Saved a copy to {dlg.FileName}";
    }

    private void GoToLine()
    {
        var text = FluentDialog.PromptText(Window.GetWindow(this), "Go to line", $"Line number (1–{_editor.Document.LineCount}):");
        if (int.TryParse(text, out int line) && line >= 1 && line <= _editor.Document.LineCount)
        {
            _editor.ScrollToLine(line);
            _editor.TextArea.Caret.Line = line;
            _editor.TextArea.Caret.Column = 1;
            _editor.TextArea.Focus();
        }
    }

    public override void ApplySettings()
    {
        var e = SettingsStore.Settings.Editor;
        _editor.FontFamily = new FontFamily(e.FontFamily + ", Cascadia Mono, Consolas");
        _editor.FontSize = e.FontSize * 96.0 / 72.0;
        _editor.WordWrap = e.WordWrap;
        _editor.ShowLineNumbers = e.ShowLineNumbers;
        _editor.Options.HighlightCurrentLine = e.HighlightCurrentLine;
        _editor.Options.IndentationSize = Math.Clamp(e.TabSize, 1, 16);
        _editor.Options.ConvertTabsToSpaces = e.ConvertTabsToSpaces;
        _editor.Options.ShowSpaces = e.ShowWhitespace;
        _editor.Options.ShowTabs = e.ShowWhitespace;
        _editor.Options.EnableHyperlinks = false;

        bool dark = ThemeService.IsDark;
        _editor.Background = Brushes.Transparent;
        _editor.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _editor.LineNumbersForeground = new SolidColorBrush(dark ? Color.FromRgb(0x85, 0x85, 0x85) : Color.FromRgb(0x6E, 0x6E, 0x6E));
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x78, 0xD4));
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(dark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x10, 0, 0, 0));
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        _editor.TextArea.Caret.CaretBrush = dark ? Brushes.White : Brushes.Black;

        if (e.SyntaxHighlighting)
        {
            var def = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(_remotePath)) ?? GuessDefinition();
            _editor.SyntaxHighlighting = def == null ? null : Adapt(def, dark);
        }
        else _editor.SyntaxHighlighting = null;
        UpdateStatus();
    }

    private IHighlightingDefinition? GuessDefinition()
    {
        var name = RemotePath.FileName(_remotePath).ToLowerInvariant();
        var first = _editor.Document.LineCount > 0 ? _editor.Document.GetText(_editor.Document.GetLineByNumber(1)) : "";
        if (first.StartsWith("<?xml")) return HighlightingManager.Instance.GetDefinition("XML");
        if (first.StartsWith("#!") && first.Contains("python")) return HighlightingManager.Instance.GetDefinition("Python");
        if (name.EndsWith(".conf") || name.EndsWith(".cfg") || name.EndsWith(".ini")) return null;
        return null;
    }

    private static readonly Dictionary<(string, bool), IHighlightingDefinition> Adapted = new();

    /// <summary>AvalonEdit's built-in colours are made for white backgrounds; lighten them for dark mode.</summary>
    private static IHighlightingDefinition Adapt(IHighlightingDefinition def, bool dark)
    {
        if (!dark) return def;
        if (Adapted.TryGetValue((def.Name, dark), out var cached)) return cached;
        foreach (var c in def.NamedHighlightingColors)
        {
            if (c.Foreground?.GetColor(null) is Color fg)
            {
                double lum = (0.299 * fg.R + 0.587 * fg.G + 0.114 * fg.B) / 255;
                if (lum < 0.55)
                {
                    double k = 0.55;
                    var light = Color.FromRgb((byte)(fg.R + (255 - fg.R) * k), (byte)(fg.G + (255 - fg.G) * k), (byte)(fg.B + (255 - fg.B) * k));
                    c.Foreground = new SimpleHighlightingBrush(light);
                }
            }
            c.Background = null;
        }
        Adapted[(def.Name, dark)] = def;
        return def;
    }

    public override bool ConfirmClose(Window owner)
    {
        if (!_dirty) return true;
        var dlg = new FluentDialog(owner, "Save changes?", $"Upload your changes to {RemotePath.FileName(_remotePath)} before closing?", "Save", "Don't save", "Cancel");
        switch (dlg.ShowDialog())
        {
            case DialogResultKind.Primary:
            {
                // Wait for the upload (pumping messages) so a failed save can keep the tab open.
                var task = SaveAsync();
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                return !_dirty;
            }
            case DialogResultKind.Secondary:
                return true;
            default:
                return false;
        }
    }

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Save", "", () => _ = SaveAsync(), true, "Ctrl+S");
        yield return Item("Reload from server", "", () => _ = ReloadAsync());
        yield return Item("Save a copy…", "", SaveLocalCopy);
        yield return Item("Go to line…", "", GoToLine, true, "Ctrl+G");
        yield return Item("Find…", "", () => _findBar.Open(replace: false), true, "Ctrl+F");
        yield return Item("Replace…", "", () => _findBar.Open(replace: true), true, "Ctrl+H");
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
}
