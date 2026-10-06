using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FluenTTY.Models;

namespace FluenTTY.Services;

/// <summary>
/// Windows 11 system backdrop (Mica / Mica Alt / Acrylic) and dark title bar through DWM — the same
/// approach as FluentPlay's WindowBackdrop. On Windows 10 the calls fail harmlessly and the window
/// keeps its solid Fluent background.
/// </summary>
public static class WindowBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>False when Windows' transparency effects are off or FLUENTTY_NO_BACKDROP is set.</summary>
    public static bool SystemAllowsBackdrop()
    {
        if (Environment.GetEnvironmentVariable("FLUENTTY_NO_BACKDROP") is { Length: > 0 }) return false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
            return value is not int i || i != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Applies the configured backdrop. Returns true when a system backdrop (Mica/Acrylic) is showing.
    /// <paramref name="dwmCaptionButtons"/>: the window draws in its caption area and relies on DWM's own
    /// min/max/close buttons (WindowChrome with UseAeroCaptionButtons). DWM paints those buttons *under*
    /// the client area, so such a window must stay see-through even without a backdrop; it then gets a
    /// DWM caption colour matching the Fluent background and paints its own content background.
    /// </summary>
    public static bool Apply(Window window, BackdropKind kind, bool dark, bool dwmCaptionButtons = false)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetDarkMode(hwnd, dark);
        var source = HwndSource.FromHwnd(hwnd);
        var solid = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3); // SolidBackgroundFillColorBase

        bool backdrop = false;
        if (kind != BackdropKind.None && SystemAllowsBackdrop())
        {
            int type = kind switch { BackdropKind.MicaAlt => 4, BackdropKind.Acrylic => 3, _ => 2 };
            backdrop = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
        }
        if (!backdrop)
        {
            int none = 1; // DWMSBT_NONE
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }
        // Without a backdrop the extended frame is filled with the caption colour: make it the app background
        // (also overrides "Show accent colour on title bars"). With a backdrop, back to the default.
        SetCaptionColor(hwnd, backdrop ? null : solid);

        if (backdrop || dwmCaptionButtons)
        {
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0 && source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
                return backdrop;
            }
            backdrop = false;
        }

        window.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = solid;
        return false;
    }

    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>Windows 11 only (fails harmlessly on 10). Null restores the system default.</summary>
    private static void SetCaptionColor(IntPtr hwnd, Color? color)
    {
        int value = color is { } c ? c.R | (c.G << 8) | (c.B << 16) : unchecked((int)0xFFFFFFFF); // COLORREF / DWMWA_COLOR_DEFAULT
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref value, sizeof(int));
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetDarkMode(hwnd, dark);
    }

    private static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }
}
