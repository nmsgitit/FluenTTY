using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FluenTTY.Files;

/// <summary>
/// Find/replace bar for the embedded editor. Replaces AvalonEdit's SearchPanel, whose template has fixed
/// 24px controls that overlap under the Fluent theme (and which has no replace). Every match is
/// highlighted; Replace all is one undo step.
/// </summary>
public sealed class FindBar : Border
{
    private const int MaxMatches = 20000;

    private readonly TextEditor _editor;
    private readonly TextBox _find, _replace;
    private readonly ToggleButton _case, _word, _regex, _expand;
    private readonly TextBlock _count;
    private readonly FrameworkElement[] _replaceParts;
    private readonly MatchRenderer _renderer = new();
    private Regex? _pattern;
    private int _anchor;

    public bool IsOpen => Visibility == Visibility.Visible;

    public FindBar(TextEditor editor)
    {
        _editor = editor;
        Visibility = Visibility.Collapsed;
        Padding = new Thickness(8, 6, 8, 6);
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        _expand = Toggle("", "Show replace", icon: true);
        _expand.Checked += (_, _) => { foreach (var p in _replaceParts!) p.Visibility = Visibility.Visible; _expand.Content = ""; };
        _expand.Unchecked += (_, _) => { foreach (var p in _replaceParts!) p.Visibility = Visibility.Collapsed; _expand.Content = ""; };

        _find = Box("Find", "Find what", out var findBox);
        _find.TextChanged += (_, _) => Search(selectFirst: true);
        _find.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Go(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        _replace = Box("Replace", "Replace with", out var replaceBox);
        _replace.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ReplaceAll(); e.Handled = true; }
            else if (e.Key == Key.Enter) { ReplaceOne(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        _case = Toggle("Aa", "Match case");
        _word = Toggle("ab", "Match whole word");
        ((TextBlock)_word.Content).TextDecorations = TextDecorations.Underline;
        _regex = Toggle(".*", "Use regular expressions");
        foreach (var t in new[] { _case, _word, _regex })
        {
            t.Checked += (_, _) => Search(selectFirst: true);
            t.Unchecked += (_, _) => Search(selectFirst: true);
        }

        _count = new TextBlock { MinWidth = 86, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        _count.SetResourceReference(StyleProperty, "Caption");

        // One grid for both rows so the Find and Replace boxes share a column (same width).
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 460 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        tools.Children.Add(_case);
        tools.Children.Add(_word);
        tools.Children.Add(_regex);
        tools.Children.Add(_count);
        tools.Children.Add(IconButton("", "Previous match (Shift+Enter, Shift+F3)", () => Go(false)));
        tools.Children.Add(IconButton("", "Next match (Enter, F3)", () => Go(true)));
        var close = IconButton("", "Close (Esc)", Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(findBox, 1);
        Grid.SetColumn(tools, 2);
        Grid.SetColumn(close, 3);
        layout.Children.Add(_expand);
        layout.Children.Add(findBox);
        layout.Children.Add(tools);
        layout.Children.Add(close);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 0, 0) };
        actions.Children.Add(TextButton("Replace", "Replace this match (Enter)", ReplaceOne));
        actions.Children.Add(TextButton("Replace all", "Replace every match (Ctrl+Alt+Enter)", ReplaceAll));
        replaceBox.Margin = new Thickness(0, 6, 0, 0);
        foreach (var part in new FrameworkElement[] { replaceBox, actions })
        {
            Grid.SetRow(part, 1);
            part.Visibility = Visibility.Collapsed;
            layout.Children.Add(part);
        }
        Grid.SetColumn(replaceBox, 1);
        Grid.SetColumn(actions, 2);
        _replaceParts = new FrameworkElement[] { replaceBox, actions };
        Child = layout;

        _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _editor.Document.TextChanged += (_, _) => { if (IsOpen) Search(selectFirst: false); };
        _editor.DocumentChanged += (_, _) => { if (IsOpen) Search(selectFirst: false); };
    }

    public void Open(bool replace)
    {
        Visibility = Visibility.Visible;
        _expand.IsChecked = replace || _expand.IsChecked == true;
        _anchor = _editor.SelectionStart;
        var sel = _editor.SelectedText;
        if (sel.Length > 0 && !sel.Contains('\n'))
        {
            _find.Text = _regex.IsChecked == true ? Regex.Escape(sel) : sel;
        }
        Search(selectFirst: false);
        var target = replace && _find.Text.Length > 0 ? _replace : _find;
        Dispatcher.BeginInvoke(() => { target.Focus(); target.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _renderer.Matches.Clear();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _editor.TextArea.Focus();
    }

    /// <summary>F3 / Shift+F3: works with the bar closed too, using the last search.</summary>
    public void Go(bool forward)
    {
        if (!IsOpen && _find.Text.Length > 0) { Visibility = Visibility.Visible; Search(selectFirst: false); }
        var m = _renderer.Matches;
        if (m.Count == 0) return;
        int selStart = _editor.SelectionStart, selEnd = selStart + _editor.SelectionLength;
        int idx;
        if (forward)
        {
            idx = m.FindIndex(x => x.Start >= selEnd);
            if (idx < 0) idx = 0;
        }
        else
        {
            idx = m.FindLastIndex(x => x.Start < selStart);
            if (idx < 0) idx = m.Count - 1;
        }
        Select(idx);
    }

    private Regex? BuildRegex()
    {
        var text = _find.Text;
        if (text.Length == 0) return null;
        var pattern = _regex.IsChecked == true ? text : Regex.Escape(text);
        if (_word.IsChecked == true) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (_case.IsChecked != true) options |= RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    private void Search(bool selectFirst)
    {
        var matches = _renderer.Matches;
        matches.Clear();
        _count.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            _pattern = BuildRegex();
            if (_pattern != null)
            {
                foreach (Match m in _pattern.Matches(_editor.Document.Text))
                {
                    if (m.Length == 0) continue; // "^", "\b"... match nothing visible
                    matches.Add((m.Index, m.Length));
                    if (matches.Count >= MaxMatches) break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            _pattern = null;
            matches.Clear();
            _count.Text = ex is RegexMatchTimeoutException ? "Too slow" : "Invalid pattern";
            _count.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

        if (selectFirst && matches.Count > 0)
        {
            // Search as you type: jump to the first match at or after where the search started.
            int idx = matches.FindIndex(x => x.Start >= _anchor);
            Select(idx < 0 ? 0 : idx);
        }
        else UpdateCount();
    }

    private void Select(int idx)
    {
        var (start, length) = _renderer.Matches[idx];
        _editor.Select(start, length);
        var loc = _editor.Document.GetLocation(start);
        _editor.ScrollTo(loc.Line, loc.Column);
        UpdateCount();
    }

    private void UpdateCount()
    {
        var m = _renderer.Matches;
        if (_find.Text.Length == 0) { _count.Text = ""; return; }
        if (m.Count == 0) { _count.Text = "No results"; return; }
        string total = m.Count >= MaxMatches ? $"{MaxMatches}+" : m.Count.ToString();
        int cur = m.FindIndex(x => x.Start == _editor.SelectionStart && x.Length == _editor.SelectionLength);
        _count.Text = cur >= 0 ? $"{cur + 1} of {total}" : $"{total} result{(m.Count == 1 ? "" : "s")}";
    }

    private string Replacement(Match m) => _regex.IsChecked == true ? m.Result(_replace.Text) : _replace.Text;

    private void ReplaceOne()
    {
        if (_pattern == null || _editor.IsReadOnly) return;
        int start = _editor.SelectionStart, length = _editor.SelectionLength;
        if (length > 0)
        {
            var m = _pattern.Match(_editor.Document.Text, start);
            if (m.Success && m.Index == start && m.Length == length)
            {
                var rep = Replacement(m);
                _editor.Document.Replace(start, length, rep);
                _editor.Select(start + rep.Length, 0);
            }
        }
        Go(true);
    }

    private void ReplaceAll()
    {
        if (_pattern == null || _editor.IsReadOnly) return;
        var all = _pattern.Matches(_editor.Document.Text).Where(m => m.Length > 0).ToList();
        if (all.Count == 0) return;
        using (_editor.Document.RunUpdate()) // one undo step
        {
            for (int i = all.Count - 1; i >= 0; i--)
                _editor.Document.Replace(all[i].Index, all[i].Length, Replacement(all[i]));
        }
        _count.Text = $"Replaced {all.Count}";
    }

    private static TextBox Box(string hintText, string name, out Grid host)
    {
        var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        // Same text inset as the Fluent TextBox template, so the hint sits exactly where typing starts.
        var hint = new TextBlock { Text = hintText, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        host = new Grid { MinWidth = 160 };
        host.Children.Add(box);
        host.Children.Add(hint);
        return box;
    }

    private static ToggleButton Toggle(string text, string tip, bool icon = false)
    {
        var t = new ToggleButton { ToolTip = tip, Width = 32, Height = 32, Margin = new Thickness(0, 0, 2, 0), Focusable = false };
        t.SetResourceReference(StyleProperty, "IconToggleButton");
        System.Windows.Automation.AutomationProperties.SetName(t, tip);
        if (icon) { t.Content = text; t.FontSize = 12; }
        else
        {
            // Text labels (Aa, ab, .*) in the UI font rather than the icon font.
            var label = new TextBlock { Text = text, FontSize = 13 };
            label.SetResourceReference(TextBlock.FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
            t.Content = label;
        }
        return t;
    }

    private static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Width = 32, Height = 32, Focusable = false };
        b.SetResourceReference(StyleProperty, "SmallIconButton");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    private static Button TextButton(string text, string tip, Action action)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 6), MinWidth = 88 };
        b.Click += (_, _) => action();
        return b;
    }

    /// <summary>Paints every match under the text (the selection layer draws the current one on top).</summary>
    private sealed class MatchRenderer : IBackgroundRenderer
    {
        public readonly List<(int Start, int Length)> Matches = new();
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xF2, 0xA6, 0x2F)));

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (Matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            int from = textView.VisualLines[0].FirstDocumentLine.Offset;
            int to = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            // Matches are in document order: binary-search the first one that can be on screen.
            int lo = 0, hi = Matches.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Matches[mid].Start + Matches[mid].Length < from) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < Matches.Count && Matches[i].Start <= to; i++)
            {
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = Matches[i].Start, Length = Matches[i].Length });
                var geometry = builder.CreateGeometry();
                if (geometry != null) dc.DrawGeometry(Fill, null, geometry);
            }
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }
}
