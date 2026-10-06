using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluenTTY.Models;

namespace FluenTTY.Terminal;

/// <summary>Look-and-feel inputs for <see cref="TerminalCanvas"/>, resolved from settings (+ session overrides).</summary>
public sealed class TerminalLook
{
    public string FontFamily = "Cascadia Mono";
    public double FontSizePt = 11;
    public FontWeight FontWeight = FontWeights.Normal;
    public FontQuality Quality = FontQuality.Default;
    public BoldStyle BoldStyle = BoldStyle.Both;
    public double LineHeight = 1.0;
    public double Padding = 8;
    public CursorShape CursorShape = CursorShape.Block;
    public bool CursorBlink = true;
    public bool BlinkText;
    public Palette Palette = Palette.FromScheme(ColorScheme.BuiltIns[0]);
    public double BackgroundOpacity = 1.0;
    public LineDrawing LineDrawing = LineDrawing.Unicode;
}

/// <summary>
/// Draws a <see cref="TerminalEmulator"/> grid (GlyphRuns on a fixed cell grid) and turns keyboard and
/// mouse input into bytes for the host. Selection, scrollback viewing and xterm mouse reporting live here.
/// </summary>
public sealed class TerminalCanvas : FrameworkElement
{
    private TerminalEmulator _emu;
    private TerminalLook _look = new();
    private AppSettings _settings;

    public TerminalEmulator Emulator => _emu;

    /// <summary>Bytes to send to the host (keyboard, paste, mouse reports).</summary>
    public event Action<byte[]>? Input;
    /// <summary>Grid size changed: cols, rows, pixel width, pixel height.</summary>
    public event Action<int, int, int, int>? GridResized;
    /// <summary>Scroll position or scrollback length changed (for the scrollbar).</summary>
    public event Action? ViewChanged;
    public event Action<Point>? ContextMenuRequested;
    /// <summary>Asked before a paste; return false to cancel. Receives the text that will be sent.</summary>
    public Func<string, bool>? ConfirmPaste { get; set; }
    /// <summary>When set, typed input goes here instead of to the host (used for in-terminal login prompts).</summary>
    public Action<string>? InputInterceptor { get; set; }
    /// <summary>Encodes typed text for the host (charset-aware); defaults to UTF-8.</summary>
    public Func<string, byte[]> EncodeText { get; set; } = s => Encoding.UTF8.GetBytes(s);
    public bool IsInputEnabled { get; set; } = true;

    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _autoScrollTimer;
    private bool _renderPending;
    private bool _cursorOn = true;
    private bool _blinkPhase = true;

    public TerminalCanvas(TerminalEmulator emu, AppSettings settings)
    {
        _emu = emu;
        _settings = settings;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.IBeam;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _renderPending = false; InvalidateVisual(); ViewChanged?.Invoke(); };

        int blinkMs = GetCaretBlinkTime();
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(blinkMs <= 0 || blinkMs > 5000 ? 530 : blinkMs) };
        _blinkTimer.Tick += (_, _) => { _cursorOn = !_cursorOn; _blinkPhase = !_blinkPhase; InvalidateVisual(); };

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyGridSize(); };

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();

        Loaded += (_, _) => { _blinkTimer.Start(); RecomputeMetrics(); ScheduleResize(); };
        Unloaded += (_, _) => { _blinkTimer.Stop(); _renderTimer.Stop(); };
        SizeChanged += (_, _) => { InvalidateVisual(); ScheduleResize(); };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetCaretBlinkTime();

    public void ReplaceEmulator(TerminalEmulator emu)
    {
        _emu = emu;
        _scrollOffset = 0;
        ClearSelection();
        ScheduleResize();
        RequestRender();
    }

    // ================================================================== appearance

    private GlyphTypeface? _gtNormal, _gtBold, _gtItalic, _gtBoldItalic;
    private Typeface _tfNormal = new("Consolas"), _tfBold = new("Consolas");
    private readonly Dictionary<GlyphTypeface, int[]> _glyphCache = new();
    private double _cellW = 8, _cellH = 16, _baseline = 12, _fontDip = 14.67, _pixelsPerDip = 1;
    private double _underlinePos = 2, _underlineThickness = 1;

    public double CellWidth => _cellW;
    public double CellHeight => _cellH;
    public TerminalLook Look => _look;

    public void SetLook(TerminalLook look, AppSettings settings)
    {
        _look = look;
        _settings = settings;
        RecomputeMetrics();
        ScheduleResize();
        _brushes.Clear();
        _cursorOn = true;
        RequestRender();
    }

    private void RecomputeMetrics()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _fontDip = _look.FontSizePt * 96.0 / 72.0;
        var family = new FontFamily(_look.FontFamily + ", Cascadia Mono, Consolas, Courier New");
        _tfNormal = new Typeface(family, FontStyles.Normal, _look.FontWeight, FontStretches.Normal);
        _tfBold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var tfItalic = new Typeface(family, FontStyles.Italic, _look.FontWeight, FontStretches.Normal);
        var tfBoldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

        _gtNormal = TryGlyph(_tfNormal) ?? TryGlyph(new Typeface("Consolas"));
        _gtBold = TryGlyph(_tfBold) ?? _gtNormal;
        _gtItalic = TryGlyph(tfItalic) ?? _gtNormal;
        _gtBoldItalic = TryGlyph(tfBoldItalic) ?? _gtBold;
        _glyphCache.Clear();

        if (_gtNormal == null) return;
        double advance = 0.6;
        if (_gtNormal.CharacterToGlyphMap.TryGetValue('M', out var gi)) advance = _gtNormal.AdvanceWidths[gi];
        double lineSpacing = Math.Max(_gtNormal.Height, family.LineSpacing);
        _cellW = Snap(advance * _fontDip);
        _cellH = Math.Max(1, Math.Ceiling(lineSpacing * _fontDip * _look.LineHeight * _pixelsPerDip) / _pixelsPerDip);
        double natural = lineSpacing * _fontDip;
        _baseline = Snap(_gtNormal.Baseline * _fontDip + (_cellH - natural) / 2);
        _underlinePos = Math.Max(1, -_gtNormal.UnderlinePosition * _fontDip);
        _underlineThickness = Math.Max(1 / _pixelsPerDip, _gtNormal.UnderlineThickness * _fontDip);
    }

    private double Snap(double v) => Math.Max(1 / _pixelsPerDip, Math.Round(v * _pixelsPerDip) / _pixelsPerDip);

    private static GlyphTypeface? TryGlyph(Typeface tf) => tf.TryGetGlyphTypeface(out var gt) ? gt : null;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RecomputeMetrics();
        ScheduleResize();
    }

    // ================================================================== sizing

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private void ScheduleResize()
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>Recomputes cols/rows from the control size and resizes the emulator.</summary>
    public void ApplyGridSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double pad = _look.Padding;
        int cols = Math.Max(10, (int)Math.Floor((ActualWidth - 2 * pad) / _cellW));
        int rows = Math.Max(2, (int)Math.Floor((ActualHeight - 2 * pad) / _cellH));
        bool changed;
        lock (_emu.SyncRoot)
        {
            changed = cols != _emu.Cols || rows != _emu.Rows || cols != Cols || rows != Rows;
            _emu.Resize(cols, rows);
        }
        Cols = cols;
        Rows = rows;
        if (changed)
        {
            GridResized?.Invoke(cols, rows, (int)(cols * _cellW * _pixelsPerDip), (int)(rows * _cellH * _pixelsPerDip));
        }
        RequestRender();
    }

    /// <summary>Size in DIPs needed to show a grid of the given size (used to size pop-out windows).</summary>
    public Size SizeForGrid(int cols, int rows) => new(cols * _cellW + 2 * _look.Padding, rows * _cellH + 2 * _look.Padding);

    // ================================================================== scrolling

    private int _scrollOffset;
    private long _lastScrolledOff;

    /// <summary>Lines scrolled back from the bottom (0 = following output).</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int max;
            lock (_emu.SyncRoot) max = _emu.ScrollbackCount;
            int v = Math.Clamp(value, 0, max);
            if (v == _scrollOffset) return;
            _scrollOffset = v;
            RequestRender();
            ViewChanged?.Invoke();
        }
    }

    public int MaxScrollOffset
    {
        get { lock (_emu.SyncRoot) return _emu.ScrollbackCount; }
    }

    /// <summary>Called after new output arrived: keeps a scrolled-back view anchored to the same text.</summary>
    public void OnOutput()
    {
        if (_scrollOffset > 0)
        {
            long now;
            int max;
            lock (_emu.SyncRoot) { now = _emu.LinesScrolledOff; max = _emu.ScrollbackCount; }
            if (_settings.Terminal.ScrollOnOutput) _scrollOffset = 0;
            else _scrollOffset = (int)Math.Clamp(_scrollOffset + (now - _lastScrolledOff), 0, max);
        }
        lock (_emu.SyncRoot) _lastScrolledOff = _emu.LinesScrolledOff;
        RequestRender();
    }

    public void RequestRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        _renderTimer.Start();
    }

    // ================================================================== rendering

    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();

    private Brush BrushFor(Color c)
    {
        if (!_brushes.TryGetValue(c, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[c] = b;
        }
        return b;
    }

    private ushort GlyphIndex(GlyphTypeface gt, int cp, out bool found)
    {
        if (cp > 0xFFFF)
        {
            found = gt.CharacterToGlyphMap.TryGetValue(cp, out var g2);
            return g2;
        }
        if (!_glyphCache.TryGetValue(gt, out var cache))
        {
            cache = new int[65536];
            Array.Fill(cache, -1);
            _glyphCache[gt] = cache;
        }
        int v = cache[cp];
        if (v == -1)
        {
            v = gt.CharacterToGlyphMap.TryGetValue(cp, out var g) ? g : -2;
            cache[cp] = v;
        }
        found = v >= 0;
        return found ? (ushort)v : (ushort)0;
    }

    private static int AsciiLineDrawing(int cp)
    {
        if (cp < 0x2500 || cp > 0x257F) return cp;
        return cp switch
        {
            0x2500 or 0x2501 or 0x2504 or 0x2505 or 0x2508 or 0x2509 or 0x254C or 0x254D or 0x2550 => '-',
            0x2502 or 0x2503 or 0x2506 or 0x2507 or 0x250A or 0x250B or 0x254E or 0x254F or 0x2551 => '|',
            _ => '+',
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pal = _look.Palette;
        var size = RenderSize;
        if (_gtNormal == null) RecomputeMetrics();
        if (_gtNormal == null) return;

        lock (_emu.SyncRoot)
        {
            bool reverse = _emu.ReverseVideo;
            var defaultBg = reverse ? pal.Foreground : pal.Background;
            var bgFill = Color.FromArgb((byte)Math.Round(255 * _look.BackgroundOpacity), defaultBg.R, defaultBg.G, defaultBg.B);
            dc.DrawRectangle(BrushFor(bgFill), null, new Rect(size));

            int rows = _emu.Rows, cols = _emu.Cols;
            int sb = _emu.ScrollbackCount;
            int offset = Math.Min(_scrollOffset, sb);
            int topIndex = sb - offset;
            double pad = _look.Padding;
            var overrides = _emu.PaletteOverrides;

            for (int r = 0; r < rows; r++)
            {
                var line = _emu.HistoryLine(topIndex + r);
                if (line == null) continue;
                long lineId = _emu.LineIdOfHistoryIndex(topIndex + r);
                DrawLine(dc, line, lineId, pad, pad + r * _cellH, cols, pal, overrides, reverse);
            }

            // Cursor
            if (offset == 0 && _emu.CursorVisible)
            {
                bool blinks = _emu.RemoteCursorBlink ?? _look.CursorBlink;
                if (!blinks || _cursorOn || !IsKeyboardFocusWithin)
                    DrawCursor(dc, pad, pal, overrides, reverse);
            }
            _lastScrolledOff = _emu.LinesScrolledOff;
        }
    }

    private void ResolveColors(in Cell c, Palette pal, Dictionary<int, int> overrides, bool reverse, out Color fg, out Color bg, out bool bold)
    {
        var flags = c.Flags;
        bold = (flags & CellFlags.Bold) != 0;
        int fgv = c.Fg, bgv = c.Bg;
        bool boldColour = bold && _look.BoldStyle != BoldStyle.Font;
        if (boldColour && fgv >= 0 && fgv < 8 && !TermColor.IsRgb(fgv)) fgv += 8;

        fg = fgv == TermColor.Default && boldColour && pal.BoldForeground.HasValue
            ? pal.BoldForeground.Value
            : pal.Resolve(fgv, true, overrides);
        bg = pal.Resolve(bgv, false, overrides);
        if (reverse)
        {
            if (fgv == TermColor.Default) fg = pal.Background;
            if (bgv == TermColor.Default) bg = pal.Foreground;
        }
        if ((flags & CellFlags.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((flags & CellFlags.Faint) != 0)
            fg = Color.FromRgb((byte)((fg.R + bg.R) / 2), (byte)((fg.G + bg.G) / 2), (byte)((fg.B + bg.B) / 2));
        if ((flags & CellFlags.Invisible) != 0 || ((flags & CellFlags.Blink) != 0 && _look.BlinkText && !_blinkPhase)) fg = bg;
        if (_look.BoldStyle == BoldStyle.Colour) bold = false;
    }

    private void DrawLine(DrawingContext dc, TermLine line, long lineId, double x0, double y, int cols, Palette pal,
        Dictionary<int, int> overrides, bool reverse)
    {
        var cells = line.Cells;
        int n = Math.Min(cols, cells.Length);
        var defaultBg = reverse ? pal.Foreground : pal.Background;
        GetSelectionRange(lineId, cols, out int selFrom, out int selTo);

        int x = 0;
        while (x < n)
        {
            ref readonly var first = ref cells[x];
            ResolveColors(first, pal, overrides, reverse, out var fg, out var bg, out bool bold);
            bool italic = (first.Flags & CellFlags.Italic) != 0;
            bool selected = x >= selFrom && x < selTo;
            int start = x;
            x++;
            while (x < n)
            {
                ref readonly var c = ref cells[x];
                if (!c.SameAttrs(first)) break;
                if ((x >= selFrom && x < selTo) != selected) break;
                x++;
            }
            int end = x; // exclusive
            double rx = x0 + start * _cellW;
            double rw = (end - start) * _cellW;

            if (bg != defaultBg || (first.Flags & CellFlags.Inverse) != 0)
                dc.DrawRectangle(BrushFor(bg), null, new Rect(rx, y, rw, _cellH));
            if (selected)
            {
                var s = pal.Selection;
                dc.DrawRectangle(BrushFor(Color.FromArgb(0x70, s.R, s.G, s.B)), null, new Rect(rx, y, rw, _cellH));
            }

            DrawText(dc, line, cells, start, end, x0, y, fg, bold, italic);

            var flags = first.Flags;
            if ((flags & (CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.Strike | CellFlags.Overline)) != 0)
            {
                var brush = BrushFor(fg);
                double uy = y + _baseline + _underlinePos;
                if (uy + _underlineThickness > y + _cellH) uy = y + _cellH - _underlineThickness;
                if ((flags & CellFlags.Underline) != 0)
                    dc.DrawRectangle(brush, null, new Rect(rx, uy, rw, _underlineThickness));
                if ((flags & CellFlags.DoubleUnderline) != 0)
                {
                    dc.DrawRectangle(brush, null, new Rect(rx, uy, rw, _underlineThickness));
                    dc.DrawRectangle(brush, null, new Rect(rx, Math.Max(y, uy - 2 * _underlineThickness), rw, _underlineThickness));
                }
                if ((flags & CellFlags.Strike) != 0)
                    dc.DrawRectangle(brush, null, new Rect(rx, y + _baseline - _fontDip * 0.3, rw, _underlineThickness));
                if ((flags & CellFlags.Overline) != 0)
                    dc.DrawRectangle(brush, null, new Rect(rx, y, rw, _underlineThickness));
            }
        }
    }

    private readonly List<ushort> _glyphs = new(256);
    private readonly List<double> _advances = new(256);
    private readonly List<(int col, string text, int width)> _fallback = new();

    private void DrawText(DrawingContext dc, TermLine line, Cell[] cells, int start, int end, double x0, double y,
        Color fg, bool bold, bool italic)
    {
        var gt = bold ? (italic ? _gtBoldItalic : _gtBold) : (italic ? _gtItalic : _gtNormal);
        if (gt == null) return;
        _glyphs.Clear();
        _advances.Clear();
        _fallback.Clear();
        bool any = false;
        ushort space = GlyphIndex(gt, ' ', out _);
        int runStart = -1;

        for (int i = start; i < end; i++)
        {
            ref readonly var c = ref cells[i];
            if ((c.Flags & CellFlags.WideTrail) != 0)
            {
                if (runStart < 0) runStart = i;
                // The lead cell's advance already covers this column.
                if (_glyphs.Count == 0) { _glyphs.Add(space); _advances.Add(_cellW); }
                continue;
            }
            int width = (c.Flags & CellFlags.WideLead) != 0 ? 2 : 1;
            int cp = c.Code == 0 ? ' ' : c.Code;
            if (_look.LineDrawing == LineDrawing.Ascii) cp = AsciiLineDrawing(cp);
            bool hasCombining = line.Combining != null && line.Combining.ContainsKey(i);
            if (runStart < 0) runStart = i;
            if (cp == ' ' && !hasCombining)
            {
                _glyphs.Add(space);
                _advances.Add(_cellW * width);
                continue;
            }
            ushort g = GlyphIndex(gt, cp, out bool found);
            if (!found || hasCombining || (width == 2 && cp > 0x2E80))
            {
                // Glyph not in the terminal font (CJK, emoji, symbols) or needs shaping: draw with WPF font fallback.
                string text = char.ConvertFromUtf32(cp) + (hasCombining ? line.Combining![i] : "");
                _fallback.Add((i, text, width));
                _glyphs.Add(space);
                _advances.Add(_cellW * width);
                continue;
            }
            _glyphs.Add(g);
            _advances.Add(_cellW * width);
            any = true;
        }

        var brush = BrushFor(fg);
        if (any && _glyphs.Count > 0)
        {
            var origin = new Point(x0 + runStart * _cellW, y + _baseline);
            var run = new GlyphRun(gt, 0, false, _fontDip, (float)_pixelsPerDip, _glyphs.ToArray(), origin,
                _advances.ToArray(), null, null, null, null, null, null);
            dc.DrawGlyphRun(brush, run);
        }

        foreach (var (col, text, width) in _fallback)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? _tfBold : _tfNormal, _fontDip, brush, _pixelsPerDip);
            double cellX = x0 + col * _cellW;
            double w = width * _cellW;
            double tx = cellX + Math.Max(0, (w - ft.WidthIncludingTrailingWhitespace) / 2);
            if (ft.WidthIncludingTrailingWhitespace > w * 1.2)
            {
                // Too wide for its cells (e.g. emoji in a narrow font): squeeze horizontally.
                double sx = w / ft.WidthIncludingTrailingWhitespace;
                dc.PushTransform(new ScaleTransform(sx, 1, cellX, 0));
                dc.DrawText(ft, new Point(cellX, y + _baseline - ft.Baseline));
                dc.Pop();
            }
            else
            {
                dc.DrawText(ft, new Point(tx, y + _baseline - ft.Baseline));
            }
        }
    }

    private void DrawCursor(DrawingContext dc, double pad, Palette pal, Dictionary<int, int> overrides, bool reverse)
    {
        int cx = _emu.CursorX, cy = _emu.CursorY;
        var line = _emu.ScreenLine(cy);
        var cell = line.Cells[Math.Min(cx, line.Length - 1)];
        int width = (cell.Flags & CellFlags.WideLead) != 0 ? 2 : 1;
        double x = pad + cx * _cellW, y = pad + cy * _cellH;
        var rect = new Rect(x, y, _cellW * width, _cellH);
        var brush = BrushFor(pal.Cursor);
        var shape = _emu.RemoteCursorShape switch
        {
            RemoteCursorShape.Block => CursorShape.Block,
            RemoteCursorShape.Underline => CursorShape.Underline,
            RemoteCursorShape.Bar => CursorShape.Bar,
            _ => _look.CursorShape,
        };
        double thick = Math.Max(2 / _pixelsPerDip, Math.Round(_cellH * 0.1 * _pixelsPerDip) / _pixelsPerDip);

        if (!IsKeyboardFocusWithin)
        {
            var pen = new Pen(brush, 1 / _pixelsPerDip);
            double h = 0.5 / _pixelsPerDip;
            dc.DrawRectangle(null, pen, new Rect(rect.X + h, rect.Y + h, rect.Width - 2 * h, rect.Height - 2 * h));
            return;
        }
        switch (shape)
        {
            case CursorShape.Block:
                dc.DrawRectangle(brush, null, rect);
                // Redraw the character under the cursor in the cursor-text colour.
                ResolveColors(cell, pal, overrides, reverse, out _, out _, out bool bold);
                DrawText(dc, line, line.Cells, cx, Math.Min(cx + width, line.Length), pad, y, pal.CursorText, bold,
                    (cell.Flags & CellFlags.Italic) != 0);
                break;
            case CursorShape.Underline:
                dc.DrawRectangle(brush, null, new Rect(x, y + _cellH - thick, _cellW * width, thick));
                break;
            case CursorShape.Bar:
                dc.DrawRectangle(brush, null, new Rect(x, y, thick, _cellH));
                break;
        }
    }

    // ================================================================== selection

    private enum SelUnit { Char, Word, Line }

    private struct SelPoint : IComparable<SelPoint>
    {
        public long Line;
        public int Col; // boundary index 0..cols
        public int CompareTo(SelPoint o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);
    }

    private bool _hasSelection;
    private bool _selecting;
    private bool _selRect;
    private SelUnit _selUnit;
    private SelPoint _anchorA, _anchorB; // anchor range (a word/line when double/triple clicked)
    private SelPoint _selStart, _selEnd;

    public bool HasSelection => _hasSelection;

    public void ClearSelection()
    {
        if (!_hasSelection && !_selecting) return;
        _hasSelection = false;
        _selecting = false;
        RequestRender();
    }

    private void GetSelectionRange(long lineId, int cols, out int from, out int to)
    {
        from = to = -1;
        if (!_hasSelection) return;
        var s = _selStart; var e = _selEnd;
        if (lineId < s.Line || lineId > e.Line) return;
        if (_selRect)
        {
            from = Math.Min(s.Col, e.Col);
            to = Math.Max(s.Col, e.Col);
            return;
        }
        from = lineId == s.Line ? s.Col : 0;
        to = lineId == e.Line ? e.Col : cols;
    }

    private SelPoint PointToSel(Point p, bool clampToText = true)
    {
        lock (_emu.SyncRoot)
        {
            double pad = _look.Padding;
            int row = (int)Math.Floor((p.Y - pad) / _cellH);
            row = Math.Clamp(row, 0, _emu.Rows - 1);
            int col = (int)Math.Round((p.X - pad) / _cellW);
            col = Math.Clamp(col, 0, _emu.Cols);
            int top = _emu.ScrollbackCount - Math.Min(_scrollOffset, _emu.ScrollbackCount);
            return new SelPoint { Line = _emu.LineIdOfHistoryIndex(top + row), Col = col };
        }
    }

    private (int col, int row) PointToCell(Point p)
    {
        double pad = _look.Padding;
        int col = (int)Math.Floor((p.X - pad) / _cellW);
        int row = (int)Math.Floor((p.Y - pad) / _cellH);
        lock (_emu.SyncRoot)
            return (Math.Clamp(col, 0, _emu.Cols - 1), Math.Clamp(row, 0, _emu.Rows - 1));
    }

    private TermLine? LineById(long id)
    {
        int idx = _emu.HistoryIndexOfLineId(id);
        return idx < 0 ? null : _emu.HistoryLine(idx);
    }

    private bool IsWordChar(int cp)
    {
        if (cp == 0) return false;
        var delims = _settings.Selection.WordDelimiters;
        return cp > 0xFFFF || delims.IndexOf((char)cp) < 0;
    }

    /// <summary>Expands a point to the word (or line) around it.</summary>
    private (SelPoint a, SelPoint b) Expand(SelPoint p, SelUnit unit)
    {
        lock (_emu.SyncRoot)
        {
            var line = LineById(p.Line);
            int cols = _emu.Cols;
            if (line == null) return (p, p);
            if (unit == SelUnit.Line)
            {
                // Include soft-wrapped continuation lines on both sides.
                long first = p.Line, last = p.Line;
                while (LineById(first - 1) is { Wrapped: true }) first--;
                while (LineById(last) is { Wrapped: true } && LineById(last + 1) != null) last++;
                return (new SelPoint { Line = first, Col = 0 }, new SelPoint { Line = last, Col = cols });
            }
            int x = Math.Min(p.Col, line.Length - 1);
            var cells = line.Cells;
            bool word = IsWordChar(cells[x].Code);
            int a = x, b = x + 1;
            if (word)
            {
                while (a > 0 && IsWordChar(cells[a - 1].Code)) a--;
                while (b < line.Length && IsWordChar(cells[b].Code)) b++;
            }
            return (new SelPoint { Line = p.Line, Col = a }, new SelPoint { Line = p.Line, Col = b });
        }
    }

    private void UpdateSelection(SelPoint current)
    {
        if (_selUnit == SelUnit.Char)
        {
            if (current.CompareTo(_anchorA) < 0) { _selStart = current; _selEnd = _anchorA; }
            else { _selStart = _anchorA; _selEnd = current; }
        }
        else
        {
            var (a, b) = Expand(current, _selUnit);
            _selStart = a.CompareTo(_anchorA) < 0 ? a : _anchorA;
            _selEnd = b.CompareTo(_anchorB) > 0 ? b : _anchorB;
        }
        if (_selRect && _selStart.Line == _selEnd.Line && _selStart.Col > _selEnd.Col)
            (_selStart.Col, _selEnd.Col) = (_selEnd.Col, _selStart.Col);
        _hasSelection = _selStart.CompareTo(_selEnd) != 0;
        RequestRender();
    }

    public string GetSelectedText()
    {
        if (!_hasSelection) return "";
        var sb = new StringBuilder();
        bool trim = _settings.Selection.TrimTrailingWhitespace;
        lock (_emu.SyncRoot)
        {
            for (long id = _selStart.Line; id <= _selEnd.Line; id++)
            {
                var line = LineById(id);
                if (line == null) continue;
                GetSelectionRange(id, _emu.Cols, out int from, out int to);
                bool last = id == _selEnd.Line;
                string text = line.GetText(from, to, trimEnd: trim && (!line.Wrapped || _selRect || last));
                sb.Append(text);
                if (!last && (_selRect || !line.Wrapped)) sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    public void SelectAll()
    {
        lock (_emu.SyncRoot)
        {
            _selStart = new SelPoint { Line = _emu.LineIdOfHistoryIndex(0), Col = 0 };
            _selEnd = new SelPoint { Line = _emu.LineIdOfHistoryIndex(_emu.ScrollbackCount + _emu.Rows - 1), Col = _emu.Cols };
        }
        _selRect = false;
        _hasSelection = true;
        RequestRender();
    }

    public string GetAllText()
    {
        var sb = new StringBuilder();
        lock (_emu.SyncRoot)
        {
            int total = _emu.ScrollbackCount + _emu.Rows;
            for (int i = 0; i < total; i++)
            {
                var line = _emu.HistoryLine(i);
                if (line == null) continue;
                sb.Append(line.GetText(trimEnd: !line.Wrapped));
                if (!line.Wrapped) sb.Append("\r\n");
            }
        }
        return sb.ToString().TrimEnd('\r', '\n') + "\r\n";
    }

    /// <summary>The visible screen as text (UI Automation Value).</summary>
    public string GetScreenText()
    {
        var sb = new StringBuilder();
        lock (_emu.SyncRoot)
        {
            int sb0 = _emu.ScrollbackCount - Math.Min(_scrollOffset, _emu.ScrollbackCount);
            for (int r = 0; r < _emu.Rows; r++) sb.Append(_emu.HistoryLine(sb0 + r)?.GetText()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Space between the control's edge and the first cell.</summary>
    public double ContentPadding => _look.Padding;

    /// <summary>
    /// Which visible cells show something — a character, or a coloured or inverse background: the ground the
    /// screensaver crabs walk on. [row, col]; <paramref name="map"/> is reused when its size still matches.
    /// </summary>
    public bool[,] GetOccupiedCells(bool[,]? map)
    {
        lock (_emu.SyncRoot)
        {
            int rows = _emu.Rows, cols = _emu.Cols;
            if (map == null || map.GetLength(0) != rows || map.GetLength(1) != cols) map = new bool[rows, cols];
            int top = _emu.ScrollbackCount - Math.Min(_scrollOffset, _emu.ScrollbackCount);
            for (int r = 0; r < rows; r++)
            {
                var cells = _emu.HistoryLine(top + r)?.Cells;
                for (int c = 0; c < cols; c++)
                {
                    bool solid = false;
                    if (cells != null && c < cells.Length)
                    {
                        ref readonly var cell = ref cells[c];
                        int code = (cell.Flags & CellFlags.WideTrail) != 0 && c > 0 ? cells[c - 1].Code : cell.Code;
                        solid = (code > ' ' && (cell.Flags & CellFlags.Invisible) == 0)
                                || cell.Bg != TermColor.Default || (cell.Flags & CellFlags.Inverse) != 0;
                    }
                    map[r, c] = solid;
                }
            }
        }
        return map;
    }

    /// <summary>Text typed through UI Automation: goes wherever keyboard input would (prompts included).</summary>
    public void TypeText(string text)
    {
        if (InputInterceptor != null) { InputInterceptor(text); return; }
        Send(text);
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    public bool CopySelection()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return false;
        TrySetClipboard(text);
        return true;
    }

    public static void TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
    }

    // ================================================================== paste

    public void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { return; }
        PasteText(text);
    }

    public void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!_settings.Selection.PasteControlCharacters)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
                if (ch >= ' ' || ch == '\r' || ch == '\t') sb.Append(ch);
            text = sb.ToString();
        }
        if (ConfirmPaste != null && !ConfirmPaste(text)) return;
        bool bracketed;
        lock (_emu.SyncRoot) bracketed = _emu.BracketedPaste;
        if (bracketed) text = "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
        if (_settings.Terminal.ScrollOnKeypress) ScrollOffset = 0;
        Send(text);
    }

    // ================================================================== keyboard

    private readonly KeyEncoder.State _keyState = new();

    private void Send(string text)
    {
        if (!IsInputEnabled) return;
        if (InputInterceptor != null) { InputInterceptor(text); return; }
        Input?.Invoke(EncodeText(text));
    }

    public void SendRaw(byte[] data)
    {
        if (!IsInputEnabled || InputInterceptor != null) return;
        Input?.Invoke(data);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var mods = Keyboard.Modifiers;
        var kb = _settings.Keyboard;

        // Local shortcuts (clipboard, scrollback) — PuTTY's Ctrl+Shift+C/V, Ctrl/Shift+Insert, Shift+PgUp/PgDn.
        if (kb.CtrlShiftCopyPaste && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (key == Key.C) { CopySelection(); e.Handled = true; return; }
            if (key == Key.V) { Paste(); e.Handled = true; return; }
        }
        if (kb.CtrlInsertShiftInsert)
        {
            if (key == Key.Insert && mods == ModifierKeys.Control) { CopySelection(); e.Handled = true; return; }
            if (key == Key.Insert && mods == ModifierKeys.Shift) { Paste(); e.Handled = true; return; }
        }
        if (mods == ModifierKeys.Shift)
        {
            int page = Math.Max(1, Rows - 1);
            switch (key)
            {
                case Key.PageUp: ScrollOffset += page; e.Handled = true; return;
                case Key.PageDown: ScrollOffset -= page; e.Handled = true; return;
            }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.Up: ScrollOffset += 1; e.Handled = true; return;
                case Key.Down: ScrollOffset -= 1; e.Handled = true; return;
                case Key.Home: ScrollOffset = MaxScrollOffset; e.Handled = true; return;
                case Key.End: ScrollOffset = 0; e.Handled = true; return;
            }
        }
        // Alt+F4 / Alt+Space go to the window when configured that way.
        if (mods == ModifierKeys.Alt && ((key == Key.F4 && kb.AltF4Closes) || (key == Key.Space && kb.AltSpaceSystemMenu))) return;
        if (mods == ModifierKeys.Alt && key == Key.Enter && _settings.Interface.FullScreenOnAltEnter) return;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.CapsLock or Key.NumLock or Key.Scroll) return;

        if (InputInterceptor != null)
        {
            switch (key)
            {
                case Key.Enter: InputInterceptor("\r"); e.Handled = true; return;
                case Key.Back: InputInterceptor("\b"); e.Handled = true; return;
                case Key.Escape: InputInterceptor("\x1b"); e.Handled = true; return;
            }
            if (mods == ModifierKeys.Control && key is Key.C or Key.D) { InputInterceptor(key == Key.C ? "\x03" : "\x04"); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && key == Key.U) { InputInterceptor("\x15"); e.Handled = true; return; }
            return;
        }

        lock (_emu.SyncRoot)
        {
            _keyState.AppCursor = _emu.ApplicationCursorKeys;
            _keyState.AppKeypad = _emu.ApplicationKeypad;
            _keyState.BackarrowSendsBs = _emu.BackarrowOverride;
        }
        var seq = KeyEncoder.Encode(key, mods, kb, _keyState);
        if (seq != null)
        {
            e.Handled = true;
            AfterUserInput();
            Send(seq);
        }
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        string text = e.Text;
        if (string.IsNullOrEmpty(text)) text = e.SystemText;
        if (string.IsNullOrEmpty(text)) return;
        // Control characters arrive through KeyDown; ignore the duplicates WPF may also report here.
        if (text.Length == 1 && text[0] < ' ' && text[0] != '\t') { e.Handled = true; return; }
        e.Handled = true;
        AfterUserInput();
        if (InputInterceptor != null) { InputInterceptor(text); return; }
        Send(text);
    }

    private void AfterUserInput()
    {
        _cursorOn = true;
        _blinkTimer.Stop();
        _blinkTimer.Start();
        if (_settings.Terminal.ScrollOnKeypress && _scrollOffset != 0) ScrollOffset = 0;
        if (_settings.Appearance.HideMouseWhileTyping) Mouse.OverrideCursor = Cursors.None;
        if (_hasSelection && !_selecting) ClearSelection();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _cursorOn = true;
        RequestRender();
        bool report;
        lock (_emu.SyncRoot) report = _emu.FocusReporting;
        if (report) SendRaw(Encoding.ASCII.GetBytes("\x1b[I"));
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        RequestRender();
        bool report;
        lock (_emu.SyncRoot) report = _emu.FocusReporting;
        if (report) SendRaw(Encoding.ASCII.GetBytes("\x1b[O"));
    }

    // ================================================================== mouse

    private int _clickCount;
    private DateTime _lastClick;
    private Point _lastClickPos;
    private (int col, int row) _lastMouseCell = (-1, -1);
    private int _mouseButtonsDown;

    private bool AppWantsMouse(MouseEventArgs e)
    {
        MouseTracking mt;
        lock (_emu.SyncRoot) mt = _emu.MouseTracking;
        if (mt == MouseTracking.None) return false;
        if (_settings.Selection.ShiftOverridesMouseReporting && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false;
        return InputInterceptor == null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Mouse.OverrideCursor == Cursors.None) Mouse.OverrideCursor = null;
        var p = e.GetPosition(this);

        if (_selecting)
        {
            UpdateSelection(PointToSel(p));
            bool outside = p.Y < 0 || p.Y > ActualHeight;
            if (outside && !_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
            else if (!outside) _autoScrollTimer.Stop();
            return;
        }

        if (AppWantsMouse(e))
        {
            MouseTracking mt;
            lock (_emu.SyncRoot) mt = _emu.MouseTracking;
            bool anyDown = e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed;
            if (mt == MouseTracking.AnyEvent || (mt == MouseTracking.ButtonEvent && anyDown))
            {
                var cell = PointToCell(p);
                if (cell == _lastMouseCell) return;
                _lastMouseCell = cell;
                int button = e.LeftButton == MouseButtonState.Pressed ? 0 : e.MiddleButton == MouseButtonState.Pressed ? 1
                    : e.RightButton == MouseButtonState.Pressed ? 2 : 3;
                SendMouse(button, true, cell, motion: true);
            }
        }
    }

    private void AutoScrollTick()
    {
        if (!_selecting) { _autoScrollTimer.Stop(); return; }
        var p = Mouse.GetPosition(this);
        if (p.Y < 0) ScrollOffset += 1 + (int)(-p.Y / _cellH);
        else if (p.Y > ActualHeight) ScrollOffset -= 1 + (int)((p.Y - ActualHeight) / _cellH);
        UpdateSelection(PointToSel(p));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var p = e.GetPosition(this);
        var mods = Keyboard.Modifiers;

        if (AppWantsMouse(e) && !(e.ChangedButton == MouseButton.Right && (mods & ModifierKeys.Control) != 0))
        {
            int button = e.ChangedButton switch { MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1 };
            if (button >= 0)
            {
                _mouseButtonsDown++;
                var cell = PointToCell(p);
                _lastMouseCell = cell;
                SendMouse(button, true, cell, motion: false);
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
            {
                var now = DateTime.Now;
                bool near = (p - _lastClickPos).Length < 4;
                _clickCount = near && (now - _lastClick).TotalMilliseconds < GetDoubleClickTime() ? _clickCount % 3 + 1 : 1;
                _lastClick = now;
                _lastClickPos = p;

                if ((mods & ModifierKeys.Shift) != 0 && _hasSelection && _clickCount == 1)
                {
                    // Shift+click extends the existing selection.
                    _selecting = true;
                    UpdateSelection(PointToSel(p));
                    CaptureMouse();
                    break;
                }

                _selUnit = _clickCount switch { 2 => SelUnit.Word, 3 => SelUnit.Line, _ => SelUnit.Char };
                _selRect = _settings.Selection.AltDragRectangular && (mods & ModifierKeys.Alt) != 0;
                var sp = PointToSel(p);
                if (_selUnit == SelUnit.Char)
                {
                    _anchorA = _anchorB = sp;
                    _hasSelection = false;
                }
                else
                {
                    var (a, b) = Expand(sp, _selUnit);
                    _anchorA = a; _anchorB = b;
                    _selStart = a; _selEnd = b;
                    _hasSelection = true;
                }
                _selecting = true;
                CaptureMouse();
                RequestRender();
                break;
            }
            case MouseButton.Right:
                if ((mods & ModifierKeys.Control) != 0 || _settings.Selection.RightClick == RightClickAction.ContextMenu)
                {
                    ContextMenuRequested?.Invoke(p);
                }
                else if (_settings.Selection.RightClick == RightClickAction.Paste)
                {
                    Paste();
                }
                else
                {
                    ExtendTo(p);
                }
                e.Handled = true;
                break;
            case MouseButton.Middle:
                if (_settings.Selection.MiddleClick == MiddleClickAction.Paste) Paste();
                else if (_settings.Selection.MiddleClick == MiddleClickAction.ExtendSelection) ExtendTo(p);
                e.Handled = true;
                break;
        }
    }

    private void ExtendTo(Point p)
    {
        if (!_hasSelection) return;
        var sp = PointToSel(p);
        // Move whichever end is closer to the click.
        if (sp.CompareTo(_selStart) < 0) _anchorA = _selEnd;
        else if (sp.CompareTo(_selEnd) > 0) _anchorA = _selStart;
        else _anchorA = _selStart;
        _anchorB = _anchorA;
        _selUnit = SelUnit.Char;
        UpdateSelection(sp);
        if (_settings.Selection.CopyOnSelect) CopySelection();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        if (_mouseButtonsDown > 0)
        {
            _mouseButtonsDown--;
            int button = e.ChangedButton switch { MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1 };
            if (button >= 0) SendMouse(button, false, PointToCell(p), motion: false);
            if (_mouseButtonsDown == 0) ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _selecting)
        {
            _selecting = false;
            _autoScrollTimer.Stop();
            ReleaseMouseCapture();
            if (_hasSelection && _settings.Selection.CopyOnSelect) CopySelection();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && _settings.Keyboard.CtrlWheelZoom)
        {
            ZoomRequested?.Invoke(e.Delta > 0 ? 1 : -1);
            return;
        }
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        if (AppWantsMouse(e))
        {
            var cell = PointToCell(e.GetPosition(this));
            for (int i = 0; i < notches; i++) SendMouse(e.Delta > 0 ? 64 : 65, true, cell, motion: false);
            return;
        }
        bool alt, appCursor;
        lock (_emu.SyncRoot) { alt = _emu.AltScreenActive; appCursor = _emu.ApplicationCursorKeys; }
        if (alt && _settings.Terminal.WheelSendsArrowsInAltScreen && InputInterceptor == null)
        {
            string seq = appCursor ? (e.Delta > 0 ? "\x1bOA" : "\x1bOB") : (e.Delta > 0 ? "\x1b[A" : "\x1b[B");
            for (int i = 0; i < notches * 3; i++) Send(seq);
            return;
        }
        int lines = SystemParameters.WheelScrollLines;
        if (lines <= 0) lines = 3;
        ScrollOffset += (e.Delta > 0 ? 1 : -1) * notches * lines;
    }

    /// <summary>+1 / -1 font size steps requested with Ctrl+wheel.</summary>
    public event Action<int>? ZoomRequested;

    private void SendMouse(int button, bool press, (int col, int row) cell, bool motion)
    {
        MouseTracking mt; MouseEncoding enc;
        lock (_emu.SyncRoot) { mt = _emu.MouseTracking; enc = _emu.MouseEncoding; }
        if (mt == MouseTracking.None) return;
        if (mt == MouseTracking.X10 && (!press || motion || button >= 64)) return;

        var mods = Keyboard.Modifiers;
        int cb = button;
        if (mt != MouseTracking.X10)
        {
            if ((mods & ModifierKeys.Shift) != 0) cb += 4;
            if ((mods & ModifierKeys.Alt) != 0) cb += 8;
            if ((mods & ModifierKeys.Control) != 0) cb += 16;
        }
        if (motion) cb += 32;
        int x = cell.col + 1, y = cell.row + 1;

        string seq;
        switch (enc)
        {
            case MouseEncoding.Sgr:
                seq = $"\x1b[<{cb};{x};{y}{(press ? 'M' : 'm')}";
                break;
            case MouseEncoding.Urxvt:
                if (!press) cb = (cb & ~3) | 3;
                seq = $"\x1b[{cb + 32};{x};{y}M";
                break;
            default:
                if (!press) cb = (cb & ~3) | 3;
                if (enc == MouseEncoding.Utf8)
                {
                    seq = "\x1b[M" + (char)(cb + 32) + char.ConvertFromUtf32(Math.Min(x + 32, 2047)) + char.ConvertFromUtf32(Math.Min(y + 32, 2047));
                    Input?.Invoke(Encoding.UTF8.GetBytes(seq));
                    return;
                }
                if (x > 223 || y > 223) return;
                Input?.Invoke(new byte[] { 0x1b, (byte)'[', (byte)'M', (byte)(cb + 32), (byte)(x + 32), (byte)(y + 32) });
                return;
        }
        Input?.Invoke(Encoding.ASCII.GetBytes(seq));
    }
}
