using System.Windows.Media;
using FluenTTY.Models;

namespace FluenTTY.Terminal;

/// <summary>Resolved colours for rendering: the scheme's 16 ANSI colours, the xterm 256-colour cube/greys and the defaults.</summary>
public sealed class Palette
{
    public readonly Color[] Table = new Color[256];
    public Color Foreground;
    public Color Background;
    public Color? BoldForeground;
    public Color Cursor;
    public Color CursorText;
    public Color Selection;

    private static readonly int[] CubeLevels = { 0, 95, 135, 175, 215, 255 };

    public static Palette FromScheme(ColorScheme scheme)
    {
        var p = new Palette
        {
            Foreground = ColorScheme.Parse(scheme.Foreground, Color.FromRgb(204, 204, 204)),
            Background = ColorScheme.Parse(scheme.Background, Color.FromRgb(12, 12, 12)),
            Cursor = ColorScheme.Parse(scheme.Cursor, Color.FromRgb(255, 255, 255)),
            CursorText = ColorScheme.Parse(scheme.CursorText, Color.FromRgb(0, 0, 0)),
            Selection = ColorScheme.Parse(scheme.Selection, Color.FromRgb(255, 255, 255)),
            BoldForeground = string.IsNullOrWhiteSpace(scheme.BoldForeground) ? null : ColorScheme.Parse(scheme.BoldForeground, Colors.White),
        };
        for (int i = 0; i < 16; i++)
            p.Table[i] = ColorScheme.Parse(i < scheme.Ansi.Length ? scheme.Ansi[i] : null, Xterm16(i));
        for (int i = 16; i < 232; i++)
        {
            int n = i - 16;
            p.Table[i] = Color.FromRgb((byte)CubeLevels[n / 36], (byte)CubeLevels[n / 6 % 6], (byte)CubeLevels[n % 6]);
        }
        for (int i = 232; i < 256; i++)
        {
            byte g = (byte)(8 + (i - 232) * 10);
            p.Table[i] = Color.FromRgb(g, g, g);
        }
        return p;
    }

    private static Color Xterm16(int i)
    {
        byte lo = 0xCD, hi = 0xFF;
        return i switch
        {
            0 => Color.FromRgb(0, 0, 0), 1 => Color.FromRgb(lo, 0, 0), 2 => Color.FromRgb(0, lo, 0), 3 => Color.FromRgb(lo, lo, 0),
            4 => Color.FromRgb(0, 0, 0xEE), 5 => Color.FromRgb(lo, 0, lo), 6 => Color.FromRgb(0, lo, lo), 7 => Color.FromRgb(0xE5, 0xE5, 0xE5),
            8 => Color.FromRgb(0x7F, 0x7F, 0x7F), 9 => Color.FromRgb(hi, 0, 0), 10 => Color.FromRgb(0, hi, 0), 11 => Color.FromRgb(hi, hi, 0),
            12 => Color.FromRgb(0x5C, 0x5C, hi), 13 => Color.FromRgb(hi, 0, hi), 14 => Color.FromRgb(0, hi, hi), _ => Color.FromRgb(hi, hi, hi),
        };
    }

    /// <summary>Nearest palette index for an RGB colour (used when true colour is switched off).</summary>
    public static int NearestIndex(int r, int g, int b, bool allow256)
    {
        if (!allow256)
        {
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                var c = Xterm16(i);
                int d = Sq(c.R - r) + Sq(c.G - g) + Sq(c.B - b);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
        int ri = NearestLevel(r), gi = NearestLevel(g), bi = NearestLevel(b);
        int cube = 16 + ri * 36 + gi * 6 + bi;
        int cubeD = Sq(CubeLevels[ri] - r) + Sq(CubeLevels[gi] - g) + Sq(CubeLevels[bi] - b);
        int avg = (r + g + b) / 3;
        int gray = Math.Clamp((avg - 8 + 5) / 10, 0, 23);
        int gv = 8 + gray * 10;
        int grayD = Sq(gv - r) + Sq(gv - g) + Sq(gv - b);
        return grayD < cubeD ? 232 + gray : cube;
    }

    private static int NearestLevel(int v)
    {
        int best = 0;
        for (int i = 1; i < 6; i++)
            if (Math.Abs(CubeLevels[i] - v) < Math.Abs(CubeLevels[best] - v)) best = i;
        return best;
    }

    private static int Sq(int x) => x * x;

    public Color Resolve(int value, bool foreground, Dictionary<int, int>? overrides = null)
    {
        if (value == TermColor.Default) return foreground ? Foreground : Background;
        if (TermColor.IsRgb(value)) return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        if (overrides != null && overrides.Count > 0 && overrides.TryGetValue(value, out var o))
            return Color.FromRgb((byte)(o >> 16), (byte)(o >> 8), (byte)o);
        return Table[value & 255];
    }
}
