using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace FluenTTY.Services;

/// <summary>
/// MobaXterm's "consolesaver" with crabs instead of penguins: after Settings › Screensaver minutes without
/// keyboard or mouse input in any FluenTTY window, crabs (Claude Code's mascot) scuttle over the terminal
/// tabs that are showing (<see cref="Views.CrabOverlay"/>). Any key, click, wheel or real mouse movement
/// sends them away; the key or click that does it is eaten so it can't land in a shell. While they're out,
/// terminal sessions get a protocol no-op every minute (<see cref="KeepaliveDue"/>) so idle connections
/// aren't dropped.
/// </summary>
public static class Screensaver
{
    /// <summary>The crabs come out (idle timeout or preview).</summary>
    public static event Action? Started;
    /// <summary>The crabs go away.</summary>
    public static event Action? Stopped;
    /// <summary>Raised once when the screensaver starts and then every minute while it runs, if keepalives are on.</summary>
    public static event Action? KeepaliveDue;

    public static bool IsActive { get; private set; }

    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromMinutes(1);
    private const int MouseSlop = 4; // device pixels the cursor may drift (desk bumps) without counting as input

    private static DateTime _lastInput = DateTime.UtcNow;
    private static DateTime _nextKeepalive;
    private static DateTime _swallowUntil;
    private static bool _preview;
    private static POINT _anchor;
    private static DispatcherTimer? _timer;

    public static void Initialize()
    {
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        GetCursorPos(out _anchor);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        SettingsStore.SettingsChanged += () =>
        {
            if (IsActive && !_preview && !SettingsStore.Settings.Interface.Screensaver) Stop();
        };
    }

    /// <summary>
    /// Settings › Screensaver › Preview: brings the crabs out now. Mouse movement doesn't stop a preview
    /// (you need to move the mouse to watch it); a key or click does. False when no terminal is showing.
    /// </summary>
    public static bool Preview()
    {
        if (!Views.CrabOverlay.AnyShowing) return false;
        if (IsActive) Stop();
        Start(preview: true);
        return true;
    }

    private static void Tick()
    {
        var s = SettingsStore.Settings.Interface;
        var now = DateTime.UtcNow;
        if (!IsActive)
        {
            if (s.Screensaver && now - _lastInput >= TimeSpan.FromMinutes(Math.Max(1, s.ScreensaverMinutes))) Start(preview: false);
            return;
        }
        if (s.ScreensaverKeepalive && now >= _nextKeepalive)
        {
            _nextKeepalive = now + KeepaliveInterval;
            KeepaliveDue?.Invoke();
        }
    }

    private static void Start(bool preview)
    {
        IsActive = true;
        _preview = preview;
        _nextKeepalive = DateTime.UtcNow; // first one on the next tick: the sessions have already been idle a while
        GetCursorPos(out _anchor);
        Diagnostics.Log(preview ? "screensaver: preview" : "screensaver: started");
        Started?.Invoke();
    }

    private static void Stop()
    {
        IsActive = false;
        _preview = false;
        _lastInput = DateTime.UtcNow;
        Diagnostics.Log("screensaver: stopped");
        Stopped?.Invoke();
    }

    private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem.Input;
        var routed = input.RoutedEvent;
        var now = DateTime.UtcNow;

        // A key press or click that woke the screensaver is eaten, including the text it would type and the
        // button release, so waking it never sends anything to a shell or presses a button.
        if (now < _swallowUntil && (routed == TextCompositionManager.PreviewTextInputStartEvent ||
                                    routed == TextCompositionManager.PreviewTextInputEvent ||
                                    routed == Mouse.PreviewMouseUpEvent))
        {
            e.Cancel();
            return;
        }

        if (routed == Mouse.PreviewMouseMoveEvent)
        {
            // WPF also raises mouse moves when content changes under a still pointer (the crabs walking past),
            // so only a real change of cursor position counts.
            if (!GetCursorPos(out var p) || (Math.Abs(p.X - _anchor.X) <= MouseSlop && Math.Abs(p.Y - _anchor.Y) <= MouseSlop)) return;
            _anchor = p;
            _lastInput = now;
            if (IsActive && !_preview) Stop();
            return;
        }
        if (routed != Keyboard.PreviewKeyDownEvent && routed != Mouse.PreviewMouseDownEvent && routed != Mouse.PreviewMouseWheelEvent) return;

        _lastInput = now;
        if (!IsActive) return;
        e.Cancel();
        _swallowUntil = now + TimeSpan.FromMilliseconds(routed == Keyboard.PreviewKeyDownEvent ? 400 : 1500);
        Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
