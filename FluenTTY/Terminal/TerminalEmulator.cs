using System.Text;

namespace FluenTTY.Terminal;

/// <summary>
/// VT100/VT220/xterm terminal emulator: turns the byte stream from the remote side into a grid of
/// cells. The parser follows the DEC state machine (vt100.net/emu/dec_ansi_parser), the behaviour
/// follows xterm with PuTTY's options on top (see PuTTY's terminal/terminal.c for the reference
/// this was checked against).
///
/// Threading: every member must be called with <see cref="SyncRoot"/> held. The network thread
/// calls <see cref="Feed"/>; the UI thread reads the grid for rendering. Events are raised on the
/// thread that caused them, with the lock held — handlers must only post work elsewhere.
/// </summary>
public sealed partial class TerminalEmulator
{
    public object SyncRoot { get; } = new();
    public TerminalOptions Options { get; private set; }

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private TermLine[] _main;
    private TermLine[] _alt;
    private readonly Scrollback _scrollback;
    public bool AltScreenActive { get; private set; }

    private int _cx, _cy;
    private bool _wrapPending;
    private CellAttr _attr = CellAttr.Default;
    private int _top, _bottom;
    private bool[] _tabs;

    // Modes
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool BracketedPaste { get; private set; }
    public bool FocusReporting { get; private set; }
    public bool AlternateScroll { get; private set; } = true;
    /// <summary>DECBKM: when set the Backspace key sends BS instead of DEL.</summary>
    public bool BackarrowSendsBackspace { get; private set; }
    public bool? BackarrowOverride { get; private set; }
    public MouseTracking MouseTracking { get; private set; }
    public MouseEncoding MouseEncoding { get; private set; }
    public RemoteCursorShape RemoteCursorShape { get; private set; }
    public bool? RemoteCursorBlink { get; private set; }
    public bool CursorBlinkMode { get; private set; }
    private bool _insertMode;
    private bool _newLineMode;
    private bool _originMode;
    private bool _autoWrap;

    // Character sets
    private const int CsAscii = 0, CsDecGraphics = 1, CsUk = 2;
    private readonly int[] _g = new int[4];
    private int _gl;
    private int _singleShift = -1;
    private bool _utf8Forced;

    private struct SavedCursor
    {
        public int X, Y;
        public bool WrapPending;
        public CellAttr Attr;
        public int G0, G1, G2, G3, Gl;
        public bool Origin;
        public bool Valid;
    }

    private SavedCursor _savedMain, _savedAlt, _saved1049;

    public string Title { get; private set; } = "";
    public string? WorkingDirectory { get; private set; }

    /// <summary>Increases on every change to the grid or cursor; the renderer redraws when it moves.</summary>
    public int Version { get; private set; }

    /// <summary>Number of lines that have ever scrolled off the top of the main screen. Stable line ids for selections.</summary>
    public long LinesScrolledOff { get; private set; }

    // Events (raised with SyncRoot held, on the calling thread)
    public event Action<byte[]>? Reply;
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<int, int>? ResizeRequested;
    public event Action<string>? ClipboardWriteRequested;
    public event Action<string>? WorkingDirectoryChanged;
    /// <summary>Raised with each chunk of printable text (for "printable output" session logs).</summary>
    public event Action<string>? PrintableOutput;

    private Decoder _decoder;
    private char[] _charBuf = new char[8192];
    private char _highSurrogate;

    public TerminalEmulator(int cols, int rows, TerminalOptions options)
    {
        Options = options;
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        _scrollback = new Scrollback(options.ScrollbackLines);
        _main = NewScreen(Cols, Rows);
        _alt = NewScreen(Cols, Rows);
        _tabs = NewTabs(Cols);
        _decoder = options.Encoding.GetDecoder();
        _autoWrap = options.AutoWrap;
        _originMode = options.DecOriginMode;
        _top = 0;
        _bottom = Rows - 1;
    }

    private TermLine[] Lines => AltScreenActive ? _alt : _main;

    private static TermLine[] NewScreen(int cols, int rows)
    {
        var s = new TermLine[rows];
        for (int i = 0; i < rows; i++) s[i] = new TermLine(cols, CellAttr.Default);
        return s;
    }

    private static bool[] NewTabs(int cols)
    {
        var t = new bool[cols];
        for (int i = 8; i < cols; i += 8) t[i] = true;
        return t;
    }

    public void ApplyOptions(TerminalOptions options)
    {
        Options = options;
        _scrollback.SetCapacity(AltScreenActive ? options.ScrollbackLines : options.ScrollbackLines);
        if (!_utf8Forced) _decoder = options.Encoding.GetDecoder();
        Version++;
    }

    // ------------------------------------------------------------------ public read access

    public int CursorX => Math.Min(_cx, Cols - 1);
    public int CursorY => _cy;
    public int ScrollbackCount => AltScreenActive ? 0 : _scrollback.Count;
    public CellAttr CurrentAttr => _attr;

    /// <summary>Line at a screen row (0 = top of the visible screen, no scrollback).</summary>
    public TermLine ScreenLine(int row) => Lines[row];

    /// <summary>
    /// Line by position in the combined history: 0..ScrollbackCount-1 are scrollback (oldest first),
    /// ScrollbackCount.. are the screen rows.
    /// </summary>
    public TermLine? HistoryLine(int index)
    {
        int sb = ScrollbackCount;
        if (index < 0) return null;
        if (index < sb) return _scrollback[index];
        index -= sb;
        return index < Rows ? Lines[index] : null;
    }

    /// <summary>Converts a stable line id (see <see cref="LinesScrolledOff"/>) into a history index, or -1 if gone.</summary>
    public int HistoryIndexOfLineId(long id)
    {
        long idx = id - (LinesScrolledOff - ScrollbackCount);
        if (idx < 0 || idx >= ScrollbackCount + Rows) return -1;
        return (int)idx;
    }

    public long LineIdOfHistoryIndex(int index) => LinesScrolledOff - ScrollbackCount + index;

    // ------------------------------------------------------------------ input

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int take = Math.Min(data.Length, 4096);
            int needed = _decoder.GetCharCount(data[..take], flush: false);
            if (needed > _charBuf.Length) _charBuf = new char[needed * 2];
            int n = _decoder.GetChars(data[..take], _charBuf, flush: false);
            ProcessChars(_charBuf.AsSpan(0, n));
            data = data[take..];
        }
        Version++;
    }

    /// <summary>Displays locally generated text (prompts, status messages) as if it came from the host.</summary>
    public void FeedText(string text)
    {
        ProcessChars(text.AsSpan());
        Version++;
    }

    private readonly StringBuilder _printBuf = new();

    private void ProcessChars(ReadOnlySpan<char> chars)
    {
        bool log = PrintableOutput != null;
        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            int cp;
            if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; continue; }
            if (char.IsLowSurrogate(ch))
            {
                if (_highSurrogate == 0) continue;
                cp = char.ConvertToUtf32(_highSurrogate, ch);
                _highSurrogate = '\0';
            }
            else cp = ch;

            // Fast path: printable text in the ground state.
            if (_state == State.Ground && cp >= 0x20 && cp != 0x7F && (cp < 0x80 || cp >= 0xA0))
            {
                Print(cp);
                if (log) _printBuf.Append(char.ConvertFromUtf32(cp));
                continue;
            }
            if (log && _state == State.Ground && (cp == '\n' || cp == '\r' || cp == '\t')) _printBuf.Append((char)cp);
            Process(cp);
        }
        if (log && _printBuf.Length > 0)
        {
            PrintableOutput!(_printBuf.ToString());
            _printBuf.Clear();
        }
    }

    // ------------------------------------------------------------------ parser

    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, CsiIgnore, Osc, OscEsc, String, StringEsc }

    private State _state = State.Ground;
    private readonly int[] _params = new int[32];
    private readonly bool[] _paramSub = new bool[32];
    private int _paramCount;
    private bool _paramStarted;
    private char _private;
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _osc = new();
    private const int MaxOsc = 1 << 20;

    private void Process(int c)
    {
        // CAN / SUB abort any sequence; ESC restarts one (except inside strings, handled below).
        if (_state is not (State.Osc or State.OscEsc or State.String or State.StringEsc))
        {
            if (c == 0x18 || c == 0x1A) { _state = State.Ground; return; }
            if (c == 0x1B) { EnterEscape(); return; }
            if (c < 0x20) { Execute(c); return; }
            if (c == 0x7F && _state != State.Ground) return; // DEL is ignored inside sequences
        }

        switch (_state)
        {
            case State.Ground:
                if (c == 0x7F) { DeleteChar(); return; }
                if (c >= 0x80 && c < 0xA0) { C1(c); return; }
                Print(c);
                return;

            case State.Escape:
                if (c >= 0x20 && c <= 0x2F) { _intermediates.Append((char)c); _state = State.EscapeIntermediate; return; }
                switch (c)
                {
                    case '[': EnterCsi(); return;
                    case ']': _osc.Clear(); _state = State.Osc; return;
                    case 'P': case 'X': case '^': case '_': _state = State.String; return;
                }
                if (c >= 0x30 && c <= 0x7E) { _state = State.Ground; EscDispatch(c); return; }
                _state = State.Ground;
                return;

            case State.EscapeIntermediate:
                if (c >= 0x20 && c <= 0x2F) { _intermediates.Append((char)c); return; }
                if (c >= 0x30 && c <= 0x7E) { _state = State.Ground; EscDispatch(c); return; }
                _state = State.Ground;
                return;

            case State.CsiEntry:
            case State.CsiParam:
                if (c >= '0' && c <= '9')
                {
                    if (_paramCount == 0) { _paramCount = 1; _params[0] = 0; _paramSub[0] = false; }
                    ref int p = ref _params[_paramCount - 1];
                    p = Math.Min(p * 10 + (c - '0'), 65535);
                    _paramStarted = true;
                    _state = State.CsiParam;
                    return;
                }
                if (c == ';' || c == ':')
                {
                    if (_paramCount == 0) { _paramCount = 1; _params[0] = 0; _paramSub[0] = false; }
                    if (_paramCount < _params.Length)
                    {
                        _params[_paramCount] = 0;
                        _paramSub[_paramCount] = c == ':';
                        _paramCount++;
                    }
                    _state = State.CsiParam;
                    return;
                }
                if (c >= '<' && c <= '?')
                {
                    if (_state == State.CsiEntry && !_paramStarted) { _private = (char)c; _state = State.CsiParam; return; }
                    _state = State.CsiIgnore;
                    return;
                }
                if (c >= 0x20 && c <= 0x2F) { _intermediates.Append((char)c); _state = State.CsiIntermediate; return; }
                if (c >= 0x40 && c <= 0x7E) { _state = State.Ground; CsiDispatch(c); return; }
                _state = State.CsiIgnore;
                return;

            case State.CsiIntermediate:
                if (c >= 0x20 && c <= 0x2F) { _intermediates.Append((char)c); return; }
                if (c >= 0x40 && c <= 0x7E) { _state = State.Ground; CsiDispatch(c); return; }
                _state = State.CsiIgnore;
                return;

            case State.CsiIgnore:
                if (c >= 0x40 && c <= 0x7E) _state = State.Ground;
                return;

            case State.Osc:
                if (c == 0x07 || c == 0x9C) { _state = State.Ground; OscDispatch(); return; }
                if (c == 0x1B) { _state = State.OscEsc; return; }
                if (c == 0x18 || c == 0x1A) { _state = State.Ground; return; }
                if (_osc.Length < MaxOsc && c >= 0x20) _osc.Append(char.ConvertFromUtf32(c));
                return;

            case State.OscEsc:
                _state = State.Ground;
                OscDispatch();
                if (c != '\\') { EnterEscape(); Process(c); }
                return;

            case State.String:
                if (c == 0x1B) { _state = State.StringEsc; return; }
                if (c == 0x9C || c == 0x18 || c == 0x1A) _state = State.Ground;
                return;

            case State.StringEsc:
                _state = State.Ground;
                if (c != '\\') { EnterEscape(); Process(c); }
                return;
        }
    }

    private void EnterEscape()
    {
        if (_state == State.Osc) { _state = State.OscEsc; return; }
        if (_state == State.String) { _state = State.StringEsc; return; }
        _intermediates.Clear();
        _state = State.Escape;
    }

    private void EnterCsi()
    {
        _paramCount = 0;
        _paramStarted = false;
        _private = '\0';
        _intermediates.Clear();
        _state = State.CsiEntry;
    }

    private void C1(int c)
    {
        switch (c)
        {
            case 0x84: Index(); break;                              // IND
            case 0x85: CarriageReturn(); LineFeed(); break;         // NEL
            case 0x88: _tabs[CursorX] = true; break;                // HTS
            case 0x8D: ReverseIndex(); break;                       // RI
            case 0x90: case 0x98: case 0x9E: case 0x9F: _state = State.String; break; // DCS, SOS, PM, APC
            case 0x9B: EnterCsi(); break;                           // CSI
            case 0x9D: _osc.Clear(); _state = State.Osc; break;     // OSC
        }
    }

    private int Param(int i, int def)
    {
        if (i >= _paramCount) return def;
        int v = _params[i];
        return v == 0 ? def : v;
    }

    private int ParamRaw(int i, int def) => i < _paramCount ? _params[i] : def;

    // ------------------------------------------------------------------ C0 controls

    private void Execute(int c)
    {
        switch (c)
        {
            case 0x05: // ENQ
                if (!string.IsNullOrEmpty(Options.Answerback)) SendReply(Options.Answerback);
                break;
            case 0x07: Bell?.Invoke(); break;
            case 0x08: Backspace(); break;
            case 0x09: Tab(1); break;
            case 0x0A: case 0x0B: case 0x0C:
                LineFeed();
                if (_newLineMode || Options.LfImpliesCr) CarriageReturn();
                break;
            case 0x0D:
                CarriageReturn();
                if (Options.CrImpliesLf) LineFeed();
                break;
            case 0x0E: _gl = 1; break; // SO
            case 0x0F: _gl = 0; break; // SI
        }
    }

    private void Backspace()
    {
        if (_wrapPending) { _wrapPending = false; return; }
        if (_cx > 0) _cx--;
    }

    private void DeleteChar()
    {
        // PuTTY: "destructive backspace" when the host sends ^? (unless disabled under Features).
        if (Options.NoDestructiveBackspace) return;
        _wrapPending = false;
        if (_cx > 0)
        {
            _cx--;
            Lines[_cy].Clear(_cx, _cx + 1, EraseAttr);
        }
    }

    private void CarriageReturn()
    {
        _cx = 0;
        _wrapPending = false;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_cy == _bottom) ScrollUp(_top, _bottom, 1);
        else if (_cy < Rows - 1) _cy++;
    }

    private void Index() => LineFeed();

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_cy == _top) ScrollDown(_top, _bottom, 1);
        else if (_cy > 0) _cy--;
    }

    private void Tab(int count)
    {
        _wrapPending = false;
        while (count-- > 0)
        {
            int x = _cx + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            _cx = Math.Min(x, Cols - 1);
        }
    }

    private void BackTab(int count)
    {
        _wrapPending = false;
        while (count-- > 0 && _cx > 0)
        {
            int x = _cx - 1;
            while (x > 0 && !_tabs[x]) x--;
            _cx = x;
        }
    }

    // ------------------------------------------------------------------ printing

    private CellAttr EraseAttr => Options.BackgroundColourErase
        ? new CellAttr { Fg = _attr.Fg, Bg = _attr.Bg, Flags = _attr.Flags & CellFlags.Inverse }
        : CellAttr.Default;

    private static readonly char[] DecGraphics =
    {
        // 0x5F .. 0x7E
        ' ', '◆', '▒', '␉', '␌', '␍', '␊', '°', '±', '␤', '␋',
        '┘', '┐', '┌', '└', '┼', '⎺', '⎻', '─', '⎼', '⎽',
        '├', '┤', '┴', '┬', '│', '≤', '≥', 'π', '≠', '£', '·',
    };

    private int Translate(int c)
    {
        int set = _singleShift >= 0 ? _g[_singleShift] : _g[_gl];
        _singleShift = -1;
        if (c > 0x7E || c < 0x20) return c;
        return set switch
        {
            CsDecGraphics when c >= 0x5F => DecGraphics[c - 0x5F],
            CsUk when c == '#' => '£',
            _ => c,
        };
    }

    private void Print(int c)
    {
        c = Translate(c);
        int width = CharWidth.Of(c, Options.CjkAmbiguousWide);
        var lines = Lines;

        if (width == 0)
        {
            // Combining mark: attach to the previously written cell.
            int px = _wrapPending ? _cx : _cx - 1;
            if (px < 0) return;
            var line = lines[_cy];
            if ((line.Cells[px].Flags & CellFlags.WideTrail) != 0 && px > 0) px--;
            line.Combining ??= new Dictionary<int, string>();
            line.Combining[px] = (line.Combining.TryGetValue(px, out var s) ? s : "") + char.ConvertFromUtf32(c);
            return;
        }

        if (_wrapPending)
        {
            if (_autoWrap)
            {
                lines[_cy].Wrapped = true;
                _cx = 0;
                LineFeed();
            }
            _wrapPending = false;
        }

        if (width == 2 && _cx == Cols - 1)
        {
            if (_autoWrap)
            {
                lines[_cy].Clear(_cx, _cx + 1, EraseAttr);
                lines[_cy].Wrapped = true;
                _cx = 0;
                LineFeed();
            }
            else
            {
                width = 1; // no room: draw as narrow rather than lose it
            }
        }

        var row = lines[_cy];
        var cells = row.Cells;
        if (_insertMode)
        {
            int move = Cols - _cx - width;
            if (move > 0) Array.Copy(cells, _cx, cells, _cx + width, move);
        }

        FixWideOverlap(row, _cx, width);
        cells[_cx] = new Cell { Code = c, Fg = _attr.Fg, Bg = _attr.Bg, Flags = _attr.Flags | (width == 2 ? CellFlags.WideLead : 0) };
        row.Combining?.Remove(_cx);
        if (width == 2)
            cells[_cx + 1] = new Cell { Code = 0, Fg = _attr.Fg, Bg = _attr.Bg, Flags = _attr.Flags | CellFlags.WideTrail };

        _cx += width;
        if (_cx >= Cols)
        {
            _cx = Cols - 1;
            _wrapPending = _autoWrap;
            if (!_autoWrap) _cx = Cols - 1;
        }
    }

    /// <summary>Clears the other half of any wide character that the write at [x, x+width) would split.</summary>
    private void FixWideOverlap(TermLine row, int x, int width)
    {
        var cells = row.Cells;
        if ((cells[x].Flags & CellFlags.WideTrail) != 0 && x > 0)
            cells[x - 1] = Cell.Blank(EraseAttr);
        int end = x + width - 1;
        if (end < Cols && (cells[end].Flags & CellFlags.WideLead) != 0 && end + 1 < Cols)
            cells[end + 1] = Cell.Blank(EraseAttr);
    }

    // ------------------------------------------------------------------ scrolling

    private void ScrollUp(int top, int bottom, int n)
    {
        var lines = Lines;
        n = Math.Min(n, bottom - top + 1);
        if (n <= 0) return;
        var erase = EraseAttr;
        bool toScrollback = !AltScreenActive && top == 0;
        for (int i = 0; i < n; i++)
        {
            var gone = lines[top];
            Array.Copy(lines, top + 1, lines, top, bottom - top);
            TermLine fresh;
            if (toScrollback)
            {
                LinesScrolledOff++;
                var recycled = _scrollback.Push(gone);
                if (recycled != null && recycled.Length == Cols) { recycled.Clear(erase); fresh = recycled; }
                else fresh = new TermLine(Cols, erase);
            }
            else
            {
                gone.Clear(erase);
                fresh = gone;
            }
            lines[bottom] = fresh;
        }
    }

    private void ScrollDown(int top, int bottom, int n)
    {
        var lines = Lines;
        n = Math.Min(n, bottom - top + 1);
        if (n <= 0) return;
        var erase = EraseAttr;
        for (int i = 0; i < n; i++)
        {
            var gone = lines[bottom];
            Array.Copy(lines, top, lines, top + 1, bottom - top);
            gone.Clear(erase);
            lines[top] = gone;
        }
    }

    // ------------------------------------------------------------------ ESC dispatch

    private void EscDispatch(int c)
    {
        string inter = _intermediates.ToString();
        if (inter.Length > 0)
        {
            char i0 = inter[0];
            if (i0 is '(' or ')' or '*' or '+')
            {
                if (Options.NoRemoteCharset) return;
                int slot = i0 - '(';
                _g[slot] = c switch { '0' => CsDecGraphics, 'A' => CsUk, _ => CsAscii };
                return;
            }
            if (i0 == '#' && c == '8') { DecAlignment(); return; }
            if (i0 == '%')
            {
                if (Options.NoRemoteCharset || !Options.Utf8Override) return;
                if (c == 'G') { _utf8Forced = true; _decoder = new UTF8Encoding(false).GetDecoder(); }
                else if (c == '@') { _utf8Forced = false; _decoder = Options.Encoding.GetDecoder(); }
                return;
            }
            return;
        }

        switch (c)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E': CarriageReturn(); LineFeed(); break;
            case 'H': _tabs[CursorX] = true; break;
            case 'M': ReverseIndex(); break;
            case 'N': _singleShift = 2; break;
            case 'O': _singleShift = 3; break;
            case 'n': _gl = 2; break;
            case 'o': _gl = 3; break;
            case 'Z': SendReply("\x1b[?6c"); break;
            case 'c': FullReset(); break;
            case '=': if (!Options.NoApplicationKeypad) ApplicationKeypad = true; break;
            case '>': ApplicationKeypad = false; break;
        }
    }

    private void DecAlignment()
    {
        foreach (var line in Lines)
        {
            for (int x = 0; x < Cols; x++) line.Cells[x] = new Cell { Code = 'E', Fg = TermColor.Default, Bg = TermColor.Default };
            line.Wrapped = false;
        }
        _top = 0; _bottom = Rows - 1;
        _cx = 0; _cy = 0; _wrapPending = false;
    }

    // ------------------------------------------------------------------ CSI dispatch

    private void CsiDispatch(int final)
    {
        string inter = _intermediates.ToString();

        if (_private == '?')
        {
            switch (final)
            {
                case 'h': for (int i = 0; i < Math.Max(1, _paramCount); i++) SetDecMode(ParamRaw(i, 0), true); return;
                case 'l': for (int i = 0; i < Math.Max(1, _paramCount); i++) SetDecMode(ParamRaw(i, 0), false); return;
                case 'J': EraseDisplay(ParamRaw(0, 0)); return;
                case 'K': EraseLine(ParamRaw(0, 0)); return;
                case 'n':
                    if (ParamRaw(0, 0) == 6) SendReply($"\x1b[?{_cy + 1};{CursorX + 1}R");
                    return;
                case 'p':
                    if (inter == "$") ReportDecMode(ParamRaw(0, 0));
                    return;
                case 's': case 'r': return; // XTSAVE/XTRESTORE: not supported
            }
            return;
        }

        if (_private == '>')
        {
            if (final == 'c') SendReply("\x1b[>0;136;0c"); // same secondary DA as PuTTY
            return;
        }

        if (_private != '\0') return;

        if (inter.Length > 0)
        {
            if (inter == " " && final == 'q') { SetCursorStyle(ParamRaw(0, 0)); return; }
            if (inter == "!" && final == 'p') { SoftReset(); return; }
            if (inter == "$" && final == 'p') { SendReply($"\x1b[{ParamRaw(0, 0)};0$y"); return; }
            return;
        }

        switch (final)
        {
            case '@': InsertChars(Param(0, 1)); break;
            case 'A': CursorUp(Param(0, 1)); break;
            case 'B': case 'e': CursorDown(Param(0, 1)); break;
            case 'C': case 'a': CursorRight(Param(0, 1)); break;
            case 'D': CursorLeft(Param(0, 1)); break;
            case 'E': CursorDown(Param(0, 1)); _cx = 0; break;
            case 'F': CursorUp(Param(0, 1)); _cx = 0; break;
            case 'G': case '`': MoveTo(Param(0, 1) - 1, _cy, absoluteRow: true); break;
            case 'H': case 'f': MoveToOrigin(Param(1, 1) - 1, Param(0, 1) - 1); break;
            case 'I': Tab(Param(0, 1)); break;
            case 'J': EraseDisplay(ParamRaw(0, 0)); break;
            case 'K': EraseLine(ParamRaw(0, 0)); break;
            case 'L': InsertLines(Param(0, 1)); break;
            case 'M': DeleteLines(Param(0, 1)); break;
            case 'P': DeleteChars(Param(0, 1)); break;
            case 'S': ScrollUp(_top, _bottom, Param(0, 1)); break;
            case 'T': if (_paramCount <= 1) ScrollDown(_top, _bottom, Param(0, 1)); break;
            case 'X': EraseChars(Param(0, 1)); break;
            case 'Z': BackTab(Param(0, 1)); break;
            case 'b': RepeatLast(Param(0, 1)); break;
            case 'c': if (ParamRaw(0, 0) == 0) SendReply("\x1b[?6c"); break; // VT102, as PuTTY answers
            case 'd': MoveToOrigin(_cx, Param(0, 1) - 1, keepColumn: true); break;
            case 'g':
                int tbc = ParamRaw(0, 0);
                if (tbc == 0) _tabs[CursorX] = false;
                else if (tbc == 3) Array.Clear(_tabs);
                break;
            case 'h': for (int i = 0; i < Math.Max(1, _paramCount); i++) SetAnsiMode(ParamRaw(i, 0), true); break;
            case 'l': for (int i = 0; i < Math.Max(1, _paramCount); i++) SetAnsiMode(ParamRaw(i, 0), false); break;
            case 'm': Sgr(); break;
            case 'n':
                int dsr = ParamRaw(0, 0);
                if (dsr == 5) SendReply("\x1b[0n");
                else if (dsr == 6)
                {
                    int row = _originMode ? _cy - _top : _cy;
                    SendReply($"\x1b[{row + 1};{CursorX + 1}R");
                }
                break;
            case 'r': SetScrollRegion(Param(0, 1) - 1, Param(1, Rows) - 1); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': WindowOp(); break;
        }
    }

    private int _lastPrinted = ' ';

    private void RepeatLast(int n)
    {
        // REP repeats the last graphic character; track it cheaply from the cell left of the cursor.
        int x = _wrapPending ? _cx : _cx - 1;
        if (x >= 0 && Lines[_cy].Cells[x].Code != 0) _lastPrinted = Lines[_cy].Cells[x].Code;
        n = Math.Min(n, Cols * Rows);
        for (int i = 0; i < n; i++) Print(_lastPrinted);
    }

    private void WindowOp()
    {
        int op = ParamRaw(0, 0);
        switch (op)
        {
            case 8:
                if (Options.NoRemoteResize) return;
                int rows = ParamRaw(1, 0), cols = ParamRaw(2, 0);
                ResizeRequested?.Invoke(cols <= 0 ? Cols : cols, rows <= 0 ? Rows : rows);
                break;
            case 18: SendReply($"\x1b[8;{Rows};{Cols}t"); break;
            case 19: SendReply($"\x1b[9;{Rows};{Cols}t"); break;
            case 20: case 21:
                if (Options.TitleQuery == TitleQueryAction.None) return;
                string t = Options.TitleQuery == TitleQueryAction.Title ? Title : "";
                SendReply(op == 20 ? $"\x1b]L{t}\x1b\\" : $"\x1b]l{t}\x1b\\");
                break;
        }
    }

    private void SetCursorStyle(int ps)
    {
        switch (ps)
        {
            case 0: RemoteCursorShape = RemoteCursorShape.Default; RemoteCursorBlink = null; break;
            case 1: RemoteCursorShape = RemoteCursorShape.Block; RemoteCursorBlink = true; break;
            case 2: RemoteCursorShape = RemoteCursorShape.Block; RemoteCursorBlink = false; break;
            case 3: RemoteCursorShape = RemoteCursorShape.Underline; RemoteCursorBlink = true; break;
            case 4: RemoteCursorShape = RemoteCursorShape.Underline; RemoteCursorBlink = false; break;
            case 5: RemoteCursorShape = RemoteCursorShape.Bar; RemoteCursorBlink = true; break;
            case 6: RemoteCursorShape = RemoteCursorShape.Bar; RemoteCursorBlink = false; break;
        }
    }

    // ------------------------------------------------------------------ modes

    private void SetAnsiMode(int mode, bool on)
    {
        switch (mode)
        {
            case 4: _insertMode = on; break;
            case 20: _newLineMode = on; break;
        }
    }

    private void SetDecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on && !Options.NoApplicationCursorKeys; break;
            case 3: // DECCOLM: we don't switch to 132 columns, but xterm clears the screen and homes
                EraseDisplay(2); _top = 0; _bottom = Rows - 1; _cx = 0; _cy = 0; break;
            case 5: ReverseVideo = on; break;
            case 6: _originMode = on; MoveToOrigin(0, 0); break;
            case 7: _autoWrap = on; if (!on) _wrapPending = false; break;
            case 12: CursorBlinkMode = on; break;
            case 25: CursorVisible = on; break;
            case 66: ApplicationKeypad = on && !Options.NoApplicationKeypad; break;
            case 67: BackarrowOverride = on; break;
            case 9: SetMouse(on ? MouseTracking.X10 : MouseTracking.None); break;
            case 1000: SetMouse(on ? MouseTracking.Normal : MouseTracking.None); break;
            case 1002: SetMouse(on ? MouseTracking.ButtonEvent : MouseTracking.None); break;
            case 1003: SetMouse(on ? MouseTracking.AnyEvent : MouseTracking.None); break;
            case 1004: FocusReporting = on; break;
            case 1005: MouseEncoding = on ? MouseEncoding.Utf8 : MouseEncoding.Default; break;
            case 1006: MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.Default; break;
            case 1015: MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.Default; break;
            case 1007: AlternateScroll = on; break;
            case 47:
                if (Options.NoAltScreen) return;
                SwitchScreen(on, clearAlt: false);
                break;
            case 1047:
                if (Options.NoAltScreen) return;
                if (!on && AltScreenActive) ClearScreen(_alt);
                SwitchScreen(on, clearAlt: false);
                break;
            case 1048:
                if (on) SaveCursor(); else RestoreCursor();
                break;
            case 1049:
                if (Options.NoAltScreen) return;
                if (on)
                {
                    if (AltScreenActive) return;
                    _saved1049 = Capture();
                    SwitchScreen(true, clearAlt: true);
                }
                else
                {
                    if (!AltScreenActive) return;
                    SwitchScreen(false, clearAlt: false);
                    if (_saved1049.Valid) Apply(_saved1049);
                }
                break;
            case 2004: BracketedPaste = on && !Options.NoBracketedPaste; break;
        }
    }

    private void ReportDecMode(int mode)
    {
        bool? state = mode switch
        {
            1 => ApplicationCursorKeys,
            5 => ReverseVideo,
            6 => _originMode,
            7 => _autoWrap,
            12 => CursorBlinkMode,
            25 => CursorVisible,
            66 => ApplicationKeypad,
            1000 => MouseTracking == MouseTracking.Normal,
            1002 => MouseTracking == MouseTracking.ButtonEvent,
            1003 => MouseTracking == MouseTracking.AnyEvent,
            1004 => FocusReporting,
            1006 => MouseEncoding == MouseEncoding.Sgr,
            1049 or 1047 or 47 => AltScreenActive,
            2004 => BracketedPaste,
            _ => null,
        };
        int value = state == null ? 0 : state.Value ? 1 : 2;
        SendReply($"\x1b[?{mode};{value}$y");
    }

    private void SetMouse(MouseTracking mode)
    {
        MouseTracking = Options.NoMouseReporting ? MouseTracking.None : mode;
    }

    private void SwitchScreen(bool alt, bool clearAlt)
    {
        if (alt == AltScreenActive) { if (alt && clearAlt) ClearScreen(_alt); return; }
        AltScreenActive = alt;
        if (alt && clearAlt) ClearScreen(_alt);
        _wrapPending = false;
        _cy = Math.Min(_cy, Rows - 1);
    }

    private void ClearScreen(TermLine[] screen)
    {
        var erase = EraseAttr;
        foreach (var l in screen) l.Clear(erase);
    }

    // ------------------------------------------------------------------ cursor movement

    private void MoveTo(int x, int y, bool absoluteRow = false)
    {
        _wrapPending = false;
        _cx = Math.Clamp(x, 0, Cols - 1);
        _cy = Math.Clamp(y, 0, Rows - 1);
    }

    /// <summary>CUP/HVP/VPA: rows are relative to the scroll region in origin mode.</summary>
    private void MoveToOrigin(int x, int y, bool keepColumn = false)
    {
        _wrapPending = false;
        if (_originMode)
        {
            y = Math.Clamp(y + _top, _top, _bottom);
        }
        else y = Math.Clamp(y, 0, Rows - 1);
        _cy = y;
        if (!keepColumn) _cx = Math.Clamp(x, 0, Cols - 1);
        else _cx = Math.Min(_cx, Cols - 1);
    }

    private void CursorUp(int n)
    {
        _wrapPending = false;
        int limit = _cy >= _top ? _top : 0;
        _cy = Math.Max(limit, _cy - n);
    }

    private void CursorDown(int n)
    {
        _wrapPending = false;
        int limit = _cy <= _bottom ? _bottom : Rows - 1;
        _cy = Math.Min(limit, _cy + n);
    }

    private void CursorRight(int n)
    {
        _wrapPending = false;
        _cx = Math.Min(Cols - 1, _cx + n);
    }

    private void CursorLeft(int n)
    {
        _wrapPending = false;
        _cx = Math.Max(0, Math.Min(_cx, Cols - 1) - n);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        if (_paramCount == 0 || bottom >= Rows) bottom = Rows - 1;
        top = Math.Max(0, top);
        if (top >= bottom) return;
        _top = top;
        _bottom = bottom;
        MoveToOrigin(0, 0);
    }

    private SavedCursor Capture() => new()
    {
        X = _cx, Y = _cy, WrapPending = _wrapPending, Attr = _attr,
        G0 = _g[0], G1 = _g[1], G2 = _g[2], G3 = _g[3], Gl = _gl, Origin = _originMode, Valid = true,
    };

    private void Apply(in SavedCursor s)
    {
        _cx = Math.Min(s.X, Cols - 1);
        _cy = Math.Min(s.Y, Rows - 1);
        _wrapPending = s.WrapPending && _cx == Cols - 1;
        _attr = s.Attr;
        _g[0] = s.G0; _g[1] = s.G1; _g[2] = s.G2; _g[3] = s.G3; _gl = s.Gl;
        _originMode = s.Origin;
    }

    private void SaveCursor()
    {
        if (AltScreenActive) _savedAlt = Capture(); else _savedMain = Capture();
    }

    private void RestoreCursor()
    {
        var s = AltScreenActive ? _savedAlt : _savedMain;
        if (s.Valid) Apply(s);
        else { _cx = 0; _cy = 0; _wrapPending = false; _attr = CellAttr.Default; _originMode = false; }
    }

    // ------------------------------------------------------------------ editing

    private void EraseDisplay(int mode)
    {
        var lines = Lines;
        var erase = EraseAttr;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = _cy + 1; y < Rows; y++) lines[y].Clear(erase);
                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < _cy; y++) lines[y].Clear(erase);
                break;
            case 2:
                if (!AltScreenActive && Options.EraseToScrollback) PushScreenToScrollback();
                foreach (var l in lines) l.Clear(erase);
                break;
            case 3:
                if (!Options.NoRemoteClearScrollback) _scrollback.Clear();
                break;
        }
        _wrapPending = false;
    }

    /// <summary>PuTTY's "Push erased text into scrollback": a full-screen clear keeps what was on screen.</summary>
    private void PushScreenToScrollback()
    {
        int last = Rows - 1;
        while (last >= 0 && IsBlank(_main[last])) last--;
        for (int y = 0; y <= last; y++)
        {
            var copy = new TermLine(Cols, CellAttr.Default);
            Array.Copy(_main[y].Cells, copy.Cells, Cols);
            copy.Wrapped = _main[y].Wrapped;
            copy.Combining = _main[y].Combining == null ? null : new Dictionary<int, string>(_main[y].Combining!);
            _scrollback.Push(copy);
            LinesScrolledOff++;
        }
    }

    private static bool IsBlank(TermLine line)
    {
        foreach (ref readonly var c in line.Cells.AsSpan())
            if ((c.Code != 0 && c.Code != ' ') || c.Bg != TermColor.Default) return false;
        return true;
    }

    private void EraseLine(int mode)
    {
        var line = Lines[_cy];
        var erase = EraseAttr;
        int x = CursorX;
        switch (mode)
        {
            case 0: line.Clear(x, Cols, erase); line.Wrapped = false; break;
            case 1: line.Clear(0, x + 1, erase); break;
            case 2: line.Clear(erase); break;
        }
        _wrapPending = false;
    }

    private void EraseChars(int n)
    {
        int x = CursorX;
        Lines[_cy].Clear(x, x + n, EraseAttr);
        _wrapPending = false;
    }

    private void InsertChars(int n)
    {
        var cells = Lines[_cy].Cells;
        int x = CursorX;
        n = Math.Min(n, Cols - x);
        Array.Copy(cells, x, cells, x + n, Cols - x - n);
        Lines[_cy].Clear(x, x + n, EraseAttr);
        _wrapPending = false;
    }

    private void DeleteChars(int n)
    {
        var cells = Lines[_cy].Cells;
        int x = CursorX;
        n = Math.Min(n, Cols - x);
        Array.Copy(cells, x + n, cells, x, Cols - x - n);
        Lines[_cy].Clear(Cols - n, Cols, EraseAttr);
        _wrapPending = false;
    }

    private void InsertLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        ScrollDown(_cy, _bottom, n);
        _cx = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        // Deleting lines never feeds the scrollback (the region starts at the cursor, not the top).
        var lines = Lines;
        n = Math.Min(n, _bottom - _cy + 1);
        var erase = EraseAttr;
        for (int i = 0; i < n; i++)
        {
            var gone = lines[_cy];
            Array.Copy(lines, _cy + 1, lines, _cy, _bottom - _cy);
            gone.Clear(erase);
            lines[_bottom] = gone;
        }
        _cx = 0;
        _wrapPending = false;
    }

    // ------------------------------------------------------------------ SGR

    private void Sgr()
    {
        if (_paramCount == 0) { _attr = CellAttr.Default; return; }
        for (int i = 0; i < _paramCount; i++)
        {
            int p = _params[i];
            // Sub-parameters (colon form) belong to the parameter before them; skip stray ones.
            if (_paramSub[i]) continue;
            switch (p)
            {
                case 0: _attr = CellAttr.Default; break;
                case 1: _attr.Flags |= CellFlags.Bold; break;
                case 2: _attr.Flags |= CellFlags.Faint; break;
                case 3: _attr.Flags |= CellFlags.Italic; break;
                case 4:
                    _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    if (i + 1 < _paramCount && _paramSub[i + 1])
                    {
                        int style = _params[++i];
                        if (style == 2) _attr.Flags |= CellFlags.DoubleUnderline;
                        else if (style != 0) _attr.Flags |= CellFlags.Underline;
                    }
                    else _attr.Flags |= CellFlags.Underline;
                    break;
                case 5: case 6: _attr.Flags |= CellFlags.Blink; break;
                case 7: _attr.Flags |= CellFlags.Inverse; break;
                case 8: _attr.Flags |= CellFlags.Invisible; break;
                case 9: _attr.Flags |= CellFlags.Strike; break;
                case 21: _attr.Flags |= CellFlags.DoubleUnderline; break;
                case 22: _attr.Flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: _attr.Flags &= ~CellFlags.Italic; break;
                case 24: _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline); break;
                case 25: _attr.Flags &= ~CellFlags.Blink; break;
                case 27: _attr.Flags &= ~CellFlags.Inverse; break;
                case 28: _attr.Flags &= ~CellFlags.Invisible; break;
                case 29: _attr.Flags &= ~CellFlags.Strike; break;
                case 53: _attr.Flags |= CellFlags.Overline; break;
                case 55: _attr.Flags &= ~CellFlags.Overline; break;
                case >= 30 and <= 37: if (Options.AnsiColour) _attr.Fg = p - 30; break;
                case 39: _attr.Fg = TermColor.Default; break;
                case >= 40 and <= 47: if (Options.AnsiColour) _attr.Bg = p - 40; break;
                case 49: _attr.Bg = TermColor.Default; break;
                case >= 90 and <= 97: if (Options.AnsiColour) _attr.Fg = p - 90 + 8; break;
                case >= 100 and <= 107: if (Options.AnsiColour) _attr.Bg = p - 100 + 8; break;
                case 38: case 48: case 58:
                    int color = ParseExtendedColour(ref i, out bool ok);
                    if (!ok || !Options.AnsiColour) break;
                    if (p == 38) _attr.Fg = color;
                    else if (p == 48) _attr.Bg = color;
                    break;
                case 59: break;
            }
        }
    }

    /// <summary>Parses 38;5;n / 38;2;r;g;b and the colon forms 38:5:n / 38:2:[cs]:r:g:b.</summary>
    private int ParseExtendedColour(ref int i, out bool ok)
    {
        ok = false;
        if (i + 1 >= _paramCount) return 0;
        bool colon = _paramSub[i + 1];
        int kind = _params[i + 1];
        if (kind == 5)
        {
            if (i + 2 >= _paramCount) { i = _paramCount; return 0; }
            int idx = _params[i + 2];
            i += 2;
            if (!Options.Xterm256Colour && idx >= 16) return 0;
            ok = true;
            return Math.Clamp(idx, 0, 255);
        }
        if (kind == 2)
        {
            int start = i + 2;
            // Colon form may carry a colour-space id: 38:2:cs:r:g:b (6 values) vs 38:2:r:g:b (5).
            if (colon)
            {
                int subs = 0;
                for (int j = i + 1; j < _paramCount && _paramSub[j]; j++) subs++;
                if (subs >= 5) start++;
                if (start + 2 >= _paramCount) { i = _paramCount; return 0; }
                int r = _params[start], g = _params[start + 1], b = _params[start + 2];
                i = i + subs;
                ok = true;
                return Options.TrueColour ? TermColor.Rgb(r, g, b) : Palette.NearestIndex(r, g, b, Options.Xterm256Colour);
            }
            if (start + 2 >= _paramCount) { i = _paramCount; return 0; }
            int rr = _params[start], gg = _params[start + 1], bb = _params[start + 2];
            i = start + 2;
            ok = true;
            return Options.TrueColour ? TermColor.Rgb(rr, gg, bb) : Palette.NearestIndex(rr, gg, bb, Options.Xterm256Colour);
        }
        i++;
        return 0;
    }

    // ------------------------------------------------------------------ OSC

    private void OscDispatch()
    {
        string s = _osc.ToString();
        int semi = s.IndexOf(';');
        string cmd = semi < 0 ? s : s[..semi];
        string arg = semi < 0 ? "" : s[(semi + 1)..];
        switch (cmd)
        {
            case "0":
            case "2":
                if (Options.NoRemoteWindowTitle) return;
                Title = arg;
                TitleChanged?.Invoke(arg);
                break;
            case "4":
                OscPalette(arg);
                break;
            case "10": case "11": case "12":
                if (arg == "?") OscColourQuery?.Invoke(int.Parse(cmd), this);
                break;
            case "104":
                PaletteOverrides.Clear();
                break;
            case "7":
                if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.Scheme == "file")
                {
                    WorkingDirectory = Uri.UnescapeDataString(uri.AbsolutePath);
                    WorkingDirectoryChanged?.Invoke(WorkingDirectory);
                }
                break;
            case "52":
                if (!Options.AllowRemoteClipboardWrite) return;
                int semi2 = arg.IndexOf(';');
                if (semi2 < 0) return;
                var data = arg[(semi2 + 1)..];
                if (data == "?") return; // reading the clipboard is never allowed
                try
                {
                    var text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                    ClipboardWriteRequested?.Invoke(text);
                }
                catch (FormatException) { }
                break;
        }
    }

    /// <summary>Colours set by the host with OSC 4, keyed by palette index.</summary>
    public Dictionary<int, int> PaletteOverrides { get; } = new();

    /// <summary>Lets the owner answer OSC 10/11/12 queries with the actual scheme colours (arg: 10 fg, 11 bg, 12 cursor).</summary>
    public Action<int, TerminalEmulator>? OscColourQuery { get; set; }

    private void OscPalette(string arg)
    {
        var parts = arg.Split(';');
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out int idx) || idx < 0 || idx > 255) continue;
            var spec = parts[i + 1];
            if (spec == "?") { OscPaletteQuery?.Invoke(idx, this); continue; }
            if (TryParseXColor(spec, out int rgb)) PaletteOverrides[idx] = rgb;
        }
    }

    public Action<int, TerminalEmulator>? OscPaletteQuery { get; set; }

    public static bool TryParseXColor(string spec, out int rgb)
    {
        rgb = 0;
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var p = spec[4..].Split('/');
            if (p.Length != 3) return false;
            int Comp(string h) => h.Length switch
            {
                1 => Convert.ToInt32(h, 16) * 17,
                2 => Convert.ToInt32(h, 16),
                3 => Convert.ToInt32(h, 16) >> 4,
                4 => Convert.ToInt32(h, 16) >> 8,
                _ => throw new FormatException(),
            };
            try { rgb = TermColor.Rgb(Comp(p[0]), Comp(p[1]), Comp(p[2])); return true; }
            catch { return false; }
        }
        if (spec.StartsWith('#') && spec.Length == 7)
        {
            try { rgb = TermColor.RgbFlag | Convert.ToInt32(spec[1..], 16); return true; }
            catch { return false; }
        }
        return false;
    }

    public void SendReply(string s) => Reply?.Invoke(Encoding.UTF8.GetBytes(s));

    // ------------------------------------------------------------------ reset / resize

    private void SoftReset()
    {
        CursorVisible = true;
        _insertMode = false;
        _originMode = Options.DecOriginMode;
        _autoWrap = Options.AutoWrap;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        _top = 0; _bottom = Rows - 1;
        _attr = CellAttr.Default;
        _g[0] = _g[1] = _g[2] = _g[3] = CsAscii; _gl = 0;
        _savedMain = default; _savedAlt = default;
        BackarrowOverride = null;
        _wrapPending = false;
    }

    public void FullReset()
    {
        SoftReset();
        if (AltScreenActive) SwitchScreen(false, false);
        ClearScreen(_main);
        ClearScreen(_alt);
        _tabs = NewTabs(Cols);
        _cx = 0; _cy = 0;
        ReverseVideo = false;
        BracketedPaste = false;
        FocusReporting = false;
        MouseTracking = MouseTracking.None;
        MouseEncoding = MouseEncoding.Default;
        RemoteCursorShape = RemoteCursorShape.Default;
        RemoteCursorBlink = null;
        CursorBlinkMode = false;
        _newLineMode = false;
        PaletteOverrides.Clear();
        _utf8Forced = false;
        _decoder = Options.Encoding.GetDecoder();
        _state = State.Ground;
        Version++;
    }

    public void ClearScrollback()
    {
        _scrollback.Clear();
        Version++;
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        var erase = CellAttr.Default;
        if (cols != Cols)
        {
            foreach (var l in _main) l.Resize(cols, erase);
            foreach (var l in _alt) l.Resize(cols, erase);
            var tabs = NewTabs(cols);
            Array.Copy(_tabs, tabs, Math.Min(cols, _tabs.Length));
            _tabs = tabs;
        }

        if (rows != Rows)
        {
            int mainCursorY = AltScreenActive ? (_saved1049.Valid ? _saved1049.Y : _cy) : _cy;
            int shift = ResizeMainRows(cols, rows, mainCursorY);
            if (AltScreenActive)
            {
                if (_saved1049.Valid) _saved1049.Y = Math.Clamp(_saved1049.Y + shift, 0, rows - 1);
                _alt = ResizeSimple(_alt, cols, rows);
                _cy = Math.Min(_cy, rows - 1);
            }
            else
            {
                _cy = Math.Clamp(_cy + shift, 0, rows - 1);
                _alt = ResizeSimple(_alt, cols, rows);
            }
            _savedMain.Y = Math.Clamp(_savedMain.Y + (AltScreenActive ? 0 : shift), 0, rows - 1);
            _savedAlt.Y = Math.Min(_savedAlt.Y, rows - 1);
        }

        Cols = cols;
        Rows = rows;
        _cx = Math.Min(_cx, cols - 1);
        _top = 0;
        _bottom = rows - 1;
        _wrapPending = false;
        Version++;
    }

    /// <summary>Resizes the main screen's height. Returns how far the cursor row moved.</summary>
    private int ResizeMainRows(int cols, int rows, int cursorY)
    {
        var erase = CellAttr.Default;
        var list = new List<TermLine>(_main);
        int shift = 0;
        if (rows < list.Count)
        {
            int excess = list.Count - rows;
            // First drop blank lines below the cursor, then push lines off the top into the scrollback.
            while (excess > 0 && list.Count - 1 > cursorY && IsBlank(list[^1]))
            {
                list.RemoveAt(list.Count - 1);
                excess--;
            }
            for (int i = 0; i < excess; i++)
            {
                _scrollback.Push(list[0]);
                LinesScrolledOff++;
                list.RemoveAt(0);
                shift--;
            }
        }
        else if (rows > list.Count)
        {
            int add = rows - list.Count;
            // Pull lines back out of the scrollback (like xterm) so growing the window reveals history.
            while (add > 0 && _scrollback.Count > 0 && !AltScreenActive)
            {
                var line = _scrollback.PopNewest()!;
                line.Resize(cols, erase);
                list.Insert(0, line);
                LinesScrolledOff--;
                shift++;
                add--;
            }
            while (add-- > 0) list.Add(new TermLine(cols, erase));
        }
        _main = list.ToArray();
        return shift;
    }

    private static TermLine[] ResizeSimple(TermLine[] screen, int cols, int rows)
    {
        var next = new TermLine[rows];
        for (int i = 0; i < rows; i++)
            next[i] = i < screen.Length ? screen[i] : new TermLine(cols, CellAttr.Default);
        return next;
    }
}
