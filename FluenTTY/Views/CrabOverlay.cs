using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Terminal;

namespace FluenTTY.Views;

/// <summary>
/// The screensaver's crabs over one terminal (<see cref="Screensaver"/>). Each is Clawd, Claude Code's pixel
/// mascot, built from the same block art as its banner ( ▐▛███▜▌ / ▝▜█████▛▘ /  ▘▘ ▝▝ ). Like XPenguins, which
/// MobaXterm's penguins imitate, they drop in from the top, land on the text, scuttle along the lines, climb
/// over words in their way, fall off the ends of lines, stop to wave their claws and wander off the edges.
/// Purely visual: never hit-testable, and it only reads the screen.
/// </summary>
public sealed class CrabOverlay : FrameworkElement
{
    private static readonly HashSet<CrabOverlay> Instances = new();

    /// <summary>True when at least one terminal is on screen to show crabs on (for Settings › Preview).</summary>
    public static bool AnyShowing => Instances.Any(o => o.IsVisible && o.ActualWidth > 0);

    private const int SpriteW = 16, SpriteH = 5;
    private static readonly Brush ShellBrush = Frozen(Color.FromRgb(0xD7, 0x77, 0x57)); // Claude Code's crab orange
    private static readonly Brush EyeBrush = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1A));

    private readonly TerminalCanvas _canvas;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly List<Crab> _crabs = new();
    private readonly Dictionary<int, (Geometry Shell, Geometry Eyes)> _poses = new();
    private bool[,]? _ground;
    // Size of one sprite pixel in DIPs (whole device pixels). Twice as tall as wide, like the half-block
    // characters of the banner in a terminal cell, so the crab keeps its real proportions.
    private double _pw, _ph;

    private enum Mode { Waiting, Falling, Walking, Pausing, Climbing }

    private sealed class Crab
    {
        public double X, Y;   // top-left, DIPs
        public int Dir = 1;   // -1 left, +1 right
        public Mode Mode;
        public int Timer;     // frames left waiting or pausing
        public int Frame;
        public double Climbed;
        public bool Leaving;  // decided to walk off the edge
        public int Look;      // eye offset while pausing
    }

    public CrabOverlay(TerminalCanvas canvas)
    {
        _canvas = canvas;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Step();

        Loaded += (_, _) =>
        {
            Instances.Add(this);
            Screensaver.Started += OnStarted;
            Screensaver.Stopped += Release;
            if (Screensaver.IsActive) OnStarted();
        };
        Unloaded += (_, _) =>
        {
            Instances.Remove(this);
            Screensaver.Started -= OnStarted;
            Screensaver.Stopped -= Release;
            Release();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Release();
            else if (Screensaver.IsActive) OnStarted();
        };
    }

    private void OnStarted()
    {
        if (!IsVisible || ActualWidth <= 0 || _timer.IsEnabled) return;
        var s = SettingsStore.Settings.Interface;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double size = s.ScreensaverCrabSize switch { CrabSize.Small => 2, CrabSize.Large => 4, _ => 3 };
        _pw = Math.Max(1, Math.Round(size * dpi)) / dpi;
        _ph = Math.Max(1, Math.Round(2 * size * dpi)) / dpi;
        _poses.Clear();
        _timer.Interval = TimeSpan.FromMilliseconds(s.ScreensaverSpeed switch { CrabSpeed.Slow => 100, CrabSpeed.Fast => 35, _ => 60 });

        _crabs.Clear();
        int count = Math.Clamp(s.ScreensaverCrabs, 1, 30);
        for (int i = 0; i < count; i++)
            _crabs.Add(new Crab { Mode = Mode.Waiting, Timer = i == 0 ? 1 : _rng.Next(5, 25 + i * 15), Dir = _rng.Next(2) == 0 ? -1 : 1 });
        _timer.Start();
    }

    private void Release()
    {
        _timer.Stop();
        if (_crabs.Count == 0) return;
        _crabs.Clear();
        InvalidateVisual();
    }

    // ================================================================== simulation

    private double W => SpriteW * _pw;
    private double H => SpriteH * _ph;

    private void Step()
    {
        if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;
        _ground = _canvas.GetOccupiedCells(_ground);
        foreach (var c in _crabs) Move(c);
        InvalidateVisual();
    }

    private void Move(Crab c)
    {
        double step = _pw;
        c.Frame++;
        switch (c.Mode)
        {
            case Mode.Waiting:
                if (--c.Timer > 0) return;
                c.X = Math.Round(_rng.NextDouble() * Math.Max(0, ActualWidth - W));
                c.Y = -H;
                c.Dir = _rng.Next(2) == 0 ? -1 : 1;
                c.Leaving = false;
                c.Mode = Mode.Falling;
                return;

            case Mode.Falling:
                Fall(c, step * 2);
                return;

            case Mode.Pausing:
                if (Blocked(c.X, c.Y)) { c.Y -= step; return; } // text appeared on top of it: get out
                if (!Supported(c.X, c.Y)) { c.Mode = Mode.Falling; return; }
                if (c.Frame % 12 == 0) c.Look = _rng.Next(3) - 1;
                if (--c.Timer <= 0) { c.Mode = Mode.Walking; if (_rng.Next(3) == 0) c.Dir = -c.Dir; }
                return;

            case Mode.Climbing:
                if (Blocked(c.X, c.Y - step) || c.Y - step < -H / 2 || c.Climbed > 3 * _canvas.CellHeight)
                {
                    c.Dir = -c.Dir; // too tall: give up and turn round
                    c.Mode = Supported(c.X, c.Y) ? Mode.Walking : Mode.Falling;
                    return;
                }
                c.Y -= step;
                c.Climbed += step;
                if (!Blocked(c.X + c.Dir * step, c.Y))
                {
                    c.X += c.Dir * step;
                    c.Mode = Supported(c.X, c.Y) ? Mode.Walking : Mode.Falling;
                }
                return;

            case Mode.Walking:
                if (Blocked(c.X, c.Y)) { c.Y -= step; return; }
                double nx = c.X + c.Dir * step;
                if (!c.Leaving && (nx < 0 || nx + W > ActualWidth))
                {
                    // At the edge: usually turn round, sometimes wander off and drop back in later.
                    if (_rng.Next(10) < 6) { c.Dir = -c.Dir; return; }
                    c.Leaving = true;
                }
                if (c.Leaving && (nx + W < 0 || nx > ActualWidth))
                {
                    c.Mode = Mode.Waiting;
                    c.Timer = _rng.Next(20, 120);
                    return;
                }
                if (Blocked(nx, c.Y))
                {
                    if (!Blocked(c.X, c.Y - step)) { c.Mode = Mode.Climbing; c.Climbed = 0; }
                    else c.Dir = -c.Dir;
                    return;
                }
                c.X = nx;
                if (!Supported(c.X, c.Y)) { c.Mode = Mode.Falling; return; }
                int roll = _rng.Next(1000);
                if (roll < 12) { c.Mode = Mode.Pausing; c.Timer = _rng.Next(15, 50); c.Look = 0; }
                else if (roll < 17 && !c.Leaving) c.Dir = -c.Dir;
                return;
        }
    }

    private void Fall(Crab c, double dy)
    {
        double feet = c.Y + H, to = feet + dy;
        if (to >= ActualHeight)
        {
            Land(c, ActualHeight - H);
            return;
        }
        // Land on the first line of text whose top the feet pass.
        var g = _ground;
        double pad = _canvas.ContentPadding, ch = _canvas.CellHeight;
        if (g != null && ch > 0)
        {
            int first = Math.Max(0, (int)Math.Ceiling((feet - pad) / ch - 0.001));
            int last = Math.Min(g.GetLength(0) - 1, (int)Math.Floor((to - pad) / ch));
            for (int r = first; r <= last; r++)
            {
                double top = pad + r * ch;
                if (top > feet - 0.001 && top <= to && SolidInRow(r, c.X + 3 * _pw, c.X + 13 * _pw))
                {
                    Land(c, top - H);
                    return;
                }
            }
        }
        c.Y += dy;
    }

    private void Land(Crab c, double y)
    {
        c.Y = y;
        c.Mode = Mode.Pausing;
        c.Timer = _rng.Next(4, 14);
        c.Look = 0;
    }

    /// <summary>Feet on the bottom edge or on top of a cell with something in it.</summary>
    private bool Supported(double x, double y)
    {
        double feet = y + H;
        if (feet >= ActualHeight - 0.5) return true;
        var g = _ground;
        double pad = _canvas.ContentPadding, ch = _canvas.CellHeight;
        if (g == null || ch <= 0) return false;
        double rf = (feet - pad) / ch;
        int r = (int)Math.Round(rf);
        if (r < 0 || r >= g.GetLength(0) || Math.Abs(rf - r) * ch > 0.5) return false;
        return SolidInRow(r, x + 3 * _pw, x + 13 * _pw);
    }

    /// <summary>The crab's body (claws excepted) would overlap text.</summary>
    private bool Blocked(double x, double y)
    {
        var g = _ground;
        double pad = _canvas.ContentPadding, ch = _canvas.CellHeight;
        if (g == null || ch <= 0) return false;
        int r0 = Math.Max(0, (int)Math.Floor((y - pad) / ch));
        int r1 = Math.Min(g.GetLength(0) - 1, (int)Math.Ceiling((y + H - pad) / ch) - 1);
        for (int r = r0; r <= r1; r++)
            if (SolidInRow(r, x + _pw, x + W - _pw)) return true;
        return false;
    }

    private bool SolidInRow(int r, double x0, double x1)
    {
        var g = _ground!;
        double pad = _canvas.ContentPadding, cw = _canvas.CellWidth;
        if (cw <= 0) return false;
        int c0 = Math.Max(0, (int)Math.Floor((x0 - pad) / cw));
        int c1 = Math.Min(g.GetLength(1) - 1, (int)Math.Ceiling((x1 - pad) / cw) - 1);
        for (int c = c0; c <= c1; c++)
            if (g[r, c]) return true;
        return false;
    }

    // ================================================================== drawing

    protected override void OnRender(DrawingContext dc)
    {
        if (_crabs.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var c in _crabs)
        {
            if (c.Mode == Mode.Waiting) continue;
            bool armsUp = c.Mode is Mode.Falling or Mode.Climbing || (c.Mode == Mode.Pausing && c.Frame / 4 % 2 == 0);
            int legs = c.Mode is Mode.Walking or Mode.Climbing ? c.Frame / 2 % 2 * c.Dir : 0;
            int eye = c.Mode switch { Mode.Walking or Mode.Climbing => c.Dir, Mode.Pausing => c.Look, _ => 0 };
            var (shell, eyes) = Pose(eye, armsUp, legs);
            dc.PushTransform(new TranslateTransform(Math.Round(c.X * dpi) / dpi, Math.Round(c.Y * dpi) / dpi));
            dc.DrawGeometry(ShellBrush, null, shell);
            dc.DrawGeometry(EyeBrush, null, eyes);
            dc.Pop();
        }
    }

    /// <summary>
    /// One frame of Clawd on a 16×5 grid: rows 0–3 are the body with the eyes in row 1 and the claws on
    /// row 2 (raised to rows 1–2 when waving or falling); row 4 holds the four legs, which step sideways.
    /// </summary>
    private (Geometry Shell, Geometry Eyes) Pose(int eye, bool armsUp, int legShift)
    {
        int key = (eye + 1) * 100 + (armsUp ? 10 : 0) + (legShift + 1);
        if (_poses.TryGetValue(key, out var pose)) return pose;

        var shell = new GeometryGroup { FillRule = FillRule.Nonzero };
        var eyes = new GeometryGroup();
        void Px(GeometryGroup g, int x, int y) => g.Children.Add(new RectangleGeometry(new Rect(x * _pw, y * _ph, _pw, _ph)));

        for (int x = 2; x <= 13; x++)
        {
            Px(shell, x, 0);
            Px(shell, x, 3);
            if (x != 4 + eye && x != 11 + eye) Px(shell, x, 1);
        }
        for (int x = 1; x <= 14; x++) Px(shell, x, 2);
        if (armsUp) { Px(shell, 0, 1); Px(shell, 15, 1); }
        else { Px(shell, 0, 2); Px(shell, 15, 2); }
        foreach (int x in new[] { 3, 5, 10, 12 }) Px(shell, x + legShift, 4);
        Px(eyes, 4 + eye, 1);
        Px(eyes, 11 + eye, 1);

        shell.Freeze();
        eyes.Freeze();
        pose = (shell, eyes);
        _poses[key] = pose;
        return pose;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
