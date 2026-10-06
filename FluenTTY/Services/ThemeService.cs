using System.Windows;
using System.Windows.Media.Animation;
using FluenTTY.Models;
using Microsoft.Win32;

namespace FluenTTY.Services;

/// <summary>
/// A window whose minimize/maximize/close are DWM's own buttons drawn inside its client area
/// (WindowChrome + UseAeroCaptionButtons). Its surface stays transparent, so when no backdrop is showing
/// it must paint an opaque background everywhere except behind those buttons.
/// </summary>
public interface IDwmCaptionWindow
{
    void BackdropApplied(bool backdrop);
}

/// <summary>Light/dark theme, backdrop and animation preferences, applied to every open window.</summary>
public static class ThemeService
{
    public static event Action? ThemeChanged;

    /// <summary>True when Windows "app mode" is dark (what ThemeMode=System follows).</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDark => SettingsStore.Settings.Interface.Theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemIsDark(),
    };

    /// <summary>
    /// Animations follow the setting and Windows' own "Animation effects" switch.
    /// </summary>
    public static bool AnimationsEnabled =>
        SettingsStore.Settings.Interface.Animations && SystemParameters.ClientAreaAnimation;

    private static AppTheme? _applied;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        SettingsStore.SettingsChanged += Apply;
        Apply();
    }

    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;
        var theme = SettingsStore.Settings.Interface.Theme;
        if (_applied != theme)
        {
            _applied = theme;
#pragma warning disable WPF0001
            app.ThemeMode = theme switch
            {
                AppTheme.Light => ThemeMode.Light,
                AppTheme.Dark => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
#pragma warning restore WPF0001
        }

        foreach (Window w in app.Windows) ApplyTo(w);
        ThemeChanged?.Invoke();
    }

    public static void ApplyTo(Window window)
    {
        if (!window.IsLoaded && new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero) return;
        var captionWindow = window as IDwmCaptionWindow;
        bool backdrop = WindowBackdrop.Apply(window, SettingsStore.Settings.Interface.Backdrop, IsDark, captionWindow != null);
        captionWindow?.BackdropApplied(backdrop);
    }

    /// <summary>Hooks a secondary window (settings, about, pop-outs) up to theme + backdrop.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTo(window);
    }

    /// <summary>Duration helper that collapses to zero when animations are off.</summary>
    public static Duration Anim(double ms) => new(TimeSpan.FromMilliseconds(AnimationsEnabled ? ms : 0));

    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
}
