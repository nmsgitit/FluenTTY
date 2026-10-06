namespace FluenTTY.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strike = 1 << 7,
    DoubleUnderline = 1 << 8,
    /// <summary>First half of a double-width character.</summary>
    WideLead = 1 << 9,
    /// <summary>Second (empty) half of a double-width character.</summary>
    WideTrail = 1 << 10,
    Overline = 1 << 11,

    /// <summary>Flags that are part of the "pen" (SGR state), as opposed to per-cell layout markers.</summary>
    AttrMask = Bold | Faint | Italic | Underline | Blink | Inverse | Invisible | Strike | DoubleUnderline | Overline,
}

/// <summary>
/// Colour values stored in cells: 0..255 are palette indexes (0..15 ANSI, 16..255 xterm cube/greys),
/// <see cref="Default"/> means the scheme's default foreground/background, and values with
/// <see cref="RgbFlag"/> set carry a 24-bit colour.
/// </summary>
public static class TermColor
{
    public const int Default = -1;
    public const int RgbFlag = 0x1000000;

    public static int Rgb(int r, int g, int b) => RgbFlag | ((r & 255) << 16) | ((g & 255) << 8) | (b & 255);
    public static bool IsRgb(int c) => c >= RgbFlag;
}

public struct CellAttr : IEquatable<CellAttr>
{
    public int Fg;
    public int Bg;
    public CellFlags Flags;

    public static readonly CellAttr Default = new() { Fg = TermColor.Default, Bg = TermColor.Default, Flags = CellFlags.None };

    public readonly bool Equals(CellAttr o) => Fg == o.Fg && Bg == o.Bg && Flags == o.Flags;
    public override readonly bool Equals(object? obj) => obj is CellAttr a && Equals(a);
    public override readonly int GetHashCode() => HashCode.Combine(Fg, Bg, Flags);
}

public struct Cell
{
    /// <summary>Unicode code point; 0 = never written (renders as a space).</summary>
    public int Code;
    public int Fg;
    public int Bg;
    public CellFlags Flags;

    public readonly bool SameAttrs(in Cell o) => Fg == o.Fg && Bg == o.Bg && (Flags & CellFlags.AttrMask) == (o.Flags & CellFlags.AttrMask);

    public static Cell Blank(in CellAttr erase) => new() { Code = 0, Fg = erase.Fg, Bg = erase.Bg, Flags = CellFlags.None };
}

/// <summary>One row of cells. Lines move between the screen and the scrollback by reference.</summary>
public sealed class TermLine
{
    public Cell[] Cells;
    /// <summary>The line continued onto the next one because the text wrapped (not a hard newline).</summary>
    public bool Wrapped;
    /// <summary>Combining characters attached to a column (rare, so kept out of the cell struct).</summary>
    public Dictionary<int, string>? Combining;

    public TermLine(int cols, in CellAttr erase)
    {
        Cells = new Cell[cols];
        // A zeroed Cell means palette colour 0, not "default", so blanks are always written explicitly.
        Array.Fill(Cells, Cell.Blank(erase));
    }

    public int Length => Cells.Length;

    public void Clear(in CellAttr erase)
    {
        var blank = Cell.Blank(erase);
        Array.Fill(Cells, blank);
        Wrapped = false;
        Combining = null;
    }

    public void Clear(int from, int to, in CellAttr erase)
    {
        from = Math.Max(0, from);
        to = Math.Min(Cells.Length, to);
        if (to <= from) return;
        var blank = Cell.Blank(erase);
        Array.Fill(Cells, blank, from, to - from);
        if (Combining != null)
            for (int i = from; i < to; i++) Combining.Remove(i);
    }

    public void Resize(int cols, in CellAttr erase)
    {
        if (cols == Cells.Length) return;
        var old = Cells;
        Cells = new Cell[cols];
        Array.Copy(old, Cells, Math.Min(cols, old.Length));
        if (cols > old.Length)
        {
            var blank = Cell.Blank(erase);
            Array.Fill(Cells, blank, old.Length, cols - old.Length);
        }
        else if (cols > 0 && (Cells[cols - 1].Flags & CellFlags.WideLead) != 0)
        {
            // Don't leave half a wide character at the new right edge.
            Cells[cols - 1] = Cell.Blank(erase);
        }
        if (Combining != null)
            foreach (var k in Combining.Keys.Where(k => k >= cols).ToList()) Combining.Remove(k);
    }

    /// <summary>Text of the line with trailing blanks trimmed (for copy, search and logging).</summary>
    public string GetText(int from = 0, int to = int.MaxValue, bool trimEnd = true)
    {
        to = Math.Min(to, Cells.Length);
        var sb = new System.Text.StringBuilder(Math.Max(0, to - from));
        for (int i = Math.Max(0, from); i < to; i++)
        {
            ref readonly var c = ref Cells[i];
            if ((c.Flags & CellFlags.WideTrail) != 0) continue;
            if (c.Code == 0) sb.Append(' ');
            else sb.Append(char.ConvertFromUtf32(c.Code));
            if (Combining != null && Combining.TryGetValue(i, out var comb)) sb.Append(comb);
        }
        if (trimEnd)
        {
            int len = sb.Length;
            while (len > 0 && sb[len - 1] == ' ') len--;
            sb.Length = len;
        }
        return sb.ToString();
    }
}

/// <summary>Fixed-capacity ring of lines that scrolled off the top of the main screen.</summary>
public sealed class Scrollback
{
    private TermLine[] _ring;
    private int _start;
    private int _count;

    public Scrollback(int capacity)
    {
        _ring = new TermLine[Math.Max(0, capacity)];
    }

    public int Count => _count;
    public int Capacity => _ring.Length;

    /// <summary>Oldest line is index 0.</summary>
    public TermLine this[int index] => _ring[(_start + index) % _ring.Length];

    /// <summary>Adds a line; returns the line that fell off the far end (for reuse), if any.</summary>
    public TermLine? Push(TermLine line)
    {
        if (_ring.Length == 0) return line;
        if (_count < _ring.Length)
        {
            _ring[(_start + _count) % _ring.Length] = line;
            _count++;
            return null;
        }
        var dropped = _ring[_start];
        _ring[_start] = line;
        _start = (_start + 1) % _ring.Length;
        return dropped;
    }

    /// <summary>Removes and returns the newest line (used when the screen grows taller).</summary>
    public TermLine? PopNewest()
    {
        if (_count == 0) return null;
        _count--;
        var idx = (_start + _count) % _ring.Length;
        var line = _ring[idx];
        _ring[idx] = null!;
        return line;
    }

    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        _count = 0;
    }

    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(0, capacity);
        if (capacity == _ring.Length) return;
        var keep = Math.Min(_count, capacity);
        var next = new TermLine[capacity];
        for (int i = 0; i < keep; i++) next[i] = this[_count - keep + i];
        _ring = next;
        _start = 0;
        _count = keep;
    }
}
