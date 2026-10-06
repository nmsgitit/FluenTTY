using System.Globalization;
using System.Windows.Media;

namespace FluenTTY.Models;

/// <summary>A terminal colour scheme: defaults plus the 16 ANSI colours (black..white, then bright black..bright white).</summary>
public sealed class ColorScheme
{
    public string Name { get; set; } = "";
    public string Foreground { get; set; } = "#CCCCCC";
    public string Background { get; set; } = "#0C0C0C";
    /// <summary>Optional colour for bold text in the default foreground (PuTTY's "Default Bold Foreground").</summary>
    public string? BoldForeground { get; set; }
    public string Cursor { get; set; } = "#FFFFFF";
    public string CursorText { get; set; } = "#000000";
    public string Selection { get; set; } = "#FFFFFF";
    public string[] Ansi { get; set; } = new string[16];
    public bool BuiltIn { get; set; }

    public ColorScheme Clone(string? newName = null) => new()
    {
        Name = newName ?? Name,
        Foreground = Foreground,
        Background = Background,
        BoldForeground = BoldForeground,
        Cursor = Cursor,
        CursorText = CursorText,
        Selection = Selection,
        Ansi = (string[])Ansi.Clone(),
        BuiltIn = false,
    };

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static readonly string[] AnsiNames =
    {
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "Bright black", "Bright red", "Bright green", "Bright yellow", "Bright blue", "Bright magenta", "Bright cyan", "Bright white",
    };

    private static ColorScheme Make(string name, string fg, string bg, string cursor, string cursorText, string selection, string ansi, string? boldFg = null) => new()
    {
        Name = name, Foreground = fg, Background = bg, Cursor = cursor, CursorText = cursorText, Selection = selection,
        BoldForeground = boldFg, Ansi = ansi.Split(' '), BuiltIn = true,
    };

    public const string DefaultDark = "Fluent Dark";
    public const string DefaultLight = "Fluent Light";

    public static IReadOnlyList<ColorScheme> BuiltIns { get; } = new[]
    {
        Make(DefaultDark, "#D6D6D6", "#1C1C1C", "#FFFFFF", "#1C1C1C", "#60CDFF",
            "#0C0C0C #F1707B #6CCB5F #F9E06B #60CDFF #D59DFF #56D4DD #CCCCCC #767676 #FF99A4 #9FD89F #FFF4A3 #99EBFF #E4C1FF #97E4E8 #F2F2F2"),
        Make(DefaultLight, "#1B1B1B", "#FCFCFC", "#1B1B1B", "#FCFCFC", "#005FB8",
            "#1B1B1B #C42B1C #0F7B0F #9D5D00 #005FB8 #8B2DB5 #00788A #C8C8C8 #5D5D5D #E81123 #13A10E #B58500 #0078D4 #B146C2 #0099BC #FFFFFF"),
        Make("Campbell", "#CCCCCC", "#0C0C0C", "#FFFFFF", "#0C0C0C", "#FFFFFF",
            "#0C0C0C #C50F1F #13A10E #C19C00 #0037DA #881798 #3A96DD #CCCCCC #767676 #E74856 #16C60C #F9F1A5 #3B78FF #B4009E #61D6D6 #F2F2F2"),
        Make("One Half Dark", "#DCDFE4", "#282C34", "#FFFFFF", "#282C34", "#FFFFFF",
            "#282C34 #E06C75 #98C379 #E5C07B #61AFEF #C678DD #56B6C2 #DCDFE4 #5A6374 #E06C75 #98C379 #E5C07B #61AFEF #C678DD #56B6C2 #DCDFE4"),
        Make("One Half Light", "#383A42", "#FAFAFA", "#4F525D", "#FAFAFA", "#383A42",
            "#383A42 #E45649 #50A14F #C18301 #0184BC #A626A4 #0997B3 #FAFAFA #4F525D #DF6C75 #98C379 #E4C07A #61AFEF #C577DD #56B5C1 #FFFFFF"),
        Make("Solarized Dark", "#839496", "#002B36", "#93A1A1", "#002B36", "#93A1A1",
            "#002B36 #DC322F #859900 #B58900 #268BD2 #D33682 #2AA198 #EEE8D5 #073642 #CB4B16 #586E75 #657B83 #839496 #6C71C4 #93A1A1 #FDF6E3"),
        Make("Solarized Light", "#657B83", "#FDF6E3", "#586E75", "#FDF6E3", "#586E75",
            "#002B36 #DC322F #859900 #B58900 #268BD2 #D33682 #2AA198 #EEE8D5 #073642 #CB4B16 #586E75 #657B83 #839496 #6C71C4 #93A1A1 #FDF6E3"),
        Make("Tango Dark", "#D3D7CF", "#000000", "#FFFFFF", "#000000", "#FFFFFF",
            "#000000 #CC0000 #4E9A06 #C4A000 #3465A4 #75507B #06989A #D3D7CF #555753 #EF2929 #8AE234 #FCE94F #729FCF #AD7FA8 #34E2E2 #EEEEEC"),
        Make("Tango Light", "#555753", "#FFFFFF", "#000000", "#FFFFFF", "#555753",
            "#000000 #CC0000 #4E9A06 #C4A000 #3465A4 #75507B #06989A #D3D7CF #555753 #EF2929 #8AE234 #FCE94F #729FCF #AD7FA8 #34E2E2 #EEEEEC"),
        Make("PuTTY", "#BBBBBB", "#000000", "#00FF00", "#000000", "#FFFFFF",
            "#000000 #BB0000 #00BB00 #BBBB00 #0000BB #BB00BB #00BBBB #BBBBBB #555555 #FF5555 #55FF55 #FFFF55 #5555FF #FF55FF #55FFFF #FFFFFF", "#FFFFFF"),
        Make("Vintage", "#C0C0C0", "#000000", "#FFFFFF", "#000000", "#FFFFFF",
            "#000000 #800000 #008000 #808000 #000080 #800080 #008080 #C0C0C0 #808080 #FF0000 #00FF00 #FFFF00 #0000FF #FF00FF #00FFFF #FFFFFF"),
    };
}
