using System.Text;
using System.Windows;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Terminal;

/// <summary>Maps settings (plus a session's overrides) onto the emulator's options and the renderer's look.</summary>
public static class TerminalConfig
{
    private static bool _providerRegistered;

    /// <summary>Character sets offered under Translation (PuTTY's "Remote character set").</summary>
    public static readonly string[] Charsets =
    {
        "UTF-8", "ISO-8859-1", "ISO-8859-2", "ISO-8859-3", "ISO-8859-4", "ISO-8859-5", "ISO-8859-6", "ISO-8859-7",
        "ISO-8859-8", "ISO-8859-9", "ISO-8859-13", "ISO-8859-15", "KOI8-R", "KOI8-U", "Windows-1250", "Windows-1251",
        "Windows-1252", "Windows-1253", "Windows-1254", "Windows-1255", "Windows-1256", "Windows-1257", "Windows-1258",
        "IBM437", "IBM850", "IBM852", "IBM866", "Shift_JIS", "EUC-JP", "GBK", "GB18030", "Big5", "EUC-KR",
    };

    public static Encoding GetEncoding(string? name)
    {
        if (!_providerRegistered)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
        if (string.IsNullOrWhiteSpace(name) || name.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
            return new UTF8Encoding(false);
        try { return Encoding.GetEncoding(name); }
        catch { return new UTF8Encoding(false); }
    }

    public static TerminalOptions BuildOptions(AppSettings s, SessionProfile? p)
    {
        var t = s.Terminal;
        return new TerminalOptions
        {
            AutoWrap = t.AutoWrap,
            DecOriginMode = t.DecOriginMode,
            LfImpliesCr = t.LfImpliesCr,
            CrImpliesLf = t.CrImpliesLf,
            BackgroundColourErase = t.BackgroundColourErase,
            Answerback = t.Answerback,
            EraseToScrollback = t.EraseToScrollback,
            ScrollbackLines = t.ScrollbackLines,
            Encoding = GetEncoding(p?.RemoteCharset ?? s.Translation.RemoteCharset),
            CjkAmbiguousWide = s.Translation.CjkAmbiguousWide,
            Utf8Override = s.Translation.Utf8Override,
            AnsiColour = s.Appearance.AnsiColour,
            Xterm256Colour = s.Appearance.Xterm256Colour,
            TrueColour = s.Appearance.TrueColour,
            NoApplicationCursorKeys = t.NoApplicationCursorKeys,
            NoApplicationKeypad = t.NoApplicationKeypad,
            NoMouseReporting = t.NoMouseReporting,
            NoRemoteResize = t.NoRemoteResize,
            NoAltScreen = t.NoAltScreen,
            NoRemoteWindowTitle = t.NoRemoteWindowTitle,
            NoRemoteClearScrollback = t.NoRemoteClearScrollback,
            NoDestructiveBackspace = t.NoDestructiveBackspace,
            NoRemoteCharset = t.NoRemoteCharset,
            NoBracketedPaste = t.NoBracketedPaste,
            TitleQuery = t.TitleQuery,
            AllowRemoteClipboardWrite = t.AllowRemoteClipboardWrite,
        };
    }

    public static TerminalLook BuildLook(AppSettings s, SessionProfile? p, bool dark, double zoomDelta = 0)
    {
        var a = s.Appearance;
        var schemeName = p?.ColorScheme ?? (dark ? a.SchemeDark : a.SchemeLight);
        var scheme = SettingsStore.FindScheme(schemeName, dark);
        bool backdrop = s.Interface.Backdrop != BackdropKind.None && WindowBackdrop.SystemAllowsBackdrop();
        return new TerminalLook
        {
            FontFamily = a.FontFamily,
            FontSizePt = Math.Clamp((p?.FontSize ?? a.FontSize) + zoomDelta, 5, 72),
            FontWeight = a.FontWeight switch
            {
                FontWeightKind.Light => FontWeights.Light,
                FontWeightKind.SemiLight => FontWeight.FromOpenTypeWeight(350),
                FontWeightKind.Medium => FontWeights.Medium,
                FontWeightKind.SemiBold => FontWeights.SemiBold,
                FontWeightKind.Bold => FontWeights.Bold,
                _ => FontWeights.Normal,
            },
            Quality = a.FontQuality,
            BoldStyle = a.BoldStyle,
            LineHeight = a.LineHeight,
            Padding = a.Padding,
            CursorShape = a.CursorShape,
            CursorBlink = a.CursorBlink,
            BlinkText = s.Terminal.BlinkText,
            Palette = Palette.FromScheme(scheme),
            // Opacity only means something when there's a backdrop to see through to.
            BackgroundOpacity = backdrop ? a.BackgroundOpacity / 100.0 : 1.0,
            LineDrawing = s.Translation.LineDrawing,
        };
    }
}
