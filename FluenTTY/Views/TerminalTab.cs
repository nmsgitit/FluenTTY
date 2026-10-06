using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FluenTTY.Connections;
using FluenTTY.Files;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Terminal;

namespace FluenTTY.Views;

/// <summary>
/// A terminal session tab (SSH, Telnet, Rlogin, Rsh, Raw, Serial): emulator + renderer + backend,
/// with PuTTY's session behaviours — in-terminal login prompts, local echo / line editing, logging,
/// bell handling, "close window on exit" — and, for SSH, an SFTP browser docked on the left.
/// </summary>
public sealed class TerminalTab : SessionTab, IConnectionUi
{
    private readonly TerminalEmulator _emu;
    private readonly TerminalCanvas _canvas;
    private readonly ScrollBar _scrollBar;
    private readonly Border _bellFlash;
    private readonly Border _infoBar;
    private readonly TextBlock _infoText;
    private readonly Grid _root;
    private readonly ColumnDefinition _sideColumn;
    private readonly GridSplitter _splitter;
    private FileBrowserView? _sftp;

    private ITerminalBackend? _backend;
    private CancellationTokenSource? _connectCts;
    private SessionLogger? _logger;
    private readonly List<string> _eventLog = new();
    private double _zoom;
    private int _outputPending;
    private bool _shutdown;
    private string _remoteTitle = "";
    private Encoding _encoding;

    public TerminalTab(SessionProfile profile)
    {
        Profile = profile;
        Glyph = SessionProfile.ProtocolGlyph(profile.Protocol);
        Title = string.IsNullOrEmpty(profile.Name) ? profile.DisplayTarget : profile.Name;
        Focusable = false;

        var s = SettingsStore.Settings;
        _encoding = TerminalConfig.GetEncoding(profile.RemoteCharset ?? s.Translation.RemoteCharset);
        _emu = new TerminalEmulator(80, 24, TerminalConfig.BuildOptions(s, profile));
        _canvas = new TerminalCanvas(_emu, s);
        _canvas.SetLook(TerminalConfig.BuildLook(s, profile, ThemeService.IsDark), s);
        _canvas.EncodeText = text => _encoding.GetBytes(text);

        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, SmallChange = 1, Minimum = 0, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Stretch };
        _scrollBar.Scroll += (_, e) => _canvas.ScrollOffset = (int)Math.Round(_scrollBar.Maximum - e.NewValue);

        _bellFlash = new Border { IsHitTestVisible = false, Opacity = 0 };
        _bellFlash.SetResourceReference(Border.BackgroundProperty, "TextFillColorPrimaryBrush");

        _infoText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _infoText.SetResourceReference(StyleProperty, "Body");
        var reconnect = new Button { Content = "Reconnect", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        reconnect.SetResourceReference(StyleProperty, "AccentButtonStyle");
        reconnect.Click += (_, _) => Reconnect();
        var closeBtn = new Button { Content = "Close tab", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        closeBtn.Click += (_, _) => RequestClose();
        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoGrid.Children.Add(_infoText);
        Grid.SetColumn(reconnect, 1); infoGrid.Children.Add(reconnect);
        Grid.SetColumn(closeBtn, 2); infoGrid.Children.Add(closeBtn);
        _infoBar = new Border
        {
            Child = infoGrid, Visibility = Visibility.Collapsed, Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1),
        };
        _infoBar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        _infoBar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var termGrid = new Grid();
        termGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        termGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        termGrid.Children.Add(_canvas);
        termGrid.Children.Add(new CrabOverlay(_canvas)); // screensaver, over the text only (not the scrollbar)
        Grid.SetColumn(_scrollBar, 1);
        termGrid.Children.Add(_scrollBar);
        termGrid.Children.Add(_bellFlash);
        termGrid.Children.Add(_infoBar);
        Grid.SetColumnSpan(_bellFlash, 2);
        Grid.SetColumnSpan(_infoBar, 2);

        _root = new Grid();
        _sideColumn = new ColumnDefinition { Width = new GridLength(0) };
        _root.ColumnDefinitions.Add(_sideColumn);
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(termGrid, 2);
        _root.Children.Add(_splitter);
        _root.Children.Add(termGrid);
        Content = _root;

        _canvas.Input += data => SendFromUser(data);
        _canvas.GridResized += (c, r, pw, ph) => _backend?.Resize(c, r, pw, ph);
        _canvas.ViewChanged += UpdateScrollBar;
        _canvas.ContextMenuRequested += p => ShowContextMenu();
        _canvas.ZoomRequested += d => { _zoom += d; ApplySettings(); };
        _canvas.ConfirmPaste = ConfirmPaste;

        _emu.Reply += data => _backend?.Send(data);
        _emu.TitleChanged += t => Dispatcher.BeginInvoke(() => { _remoteTitle = t; UpdateTitle(); });
        _emu.Bell += () => Dispatcher.BeginInvoke(OnBell);
        _emu.ResizeRequested += (c, r) => { };
        _emu.ClipboardWriteRequested += t => Dispatcher.BeginInvoke(() => TerminalCanvas.TrySetClipboard(t));
        _emu.WorkingDirectoryChanged += dir => Dispatcher.BeginInvoke(() => OnRemoteDirectory(dir));
        _emu.OscColourQuery = (which, emu) =>
        {
            var pal = _canvas.Look.Palette;
            var c = which switch { 10 => pal.Foreground, 11 => pal.Background, _ => pal.Cursor };
            emu.SendReply($"\x1b]{which};rgb:{c.R:x2}{c.R:x2}/{c.G:x2}{c.G:x2}/{c.B:x2}{c.B:x2}\x1b\\");
        };
        _emu.OscPaletteQuery = (idx, emu) =>
        {
            var c = _canvas.Look.Palette.Table[idx];
            emu.SendReply($"\x1b]4;{idx};rgb:{c.R:x2}{c.R:x2}/{c.G:x2}{c.G:x2}/{c.B:x2}{c.B:x2}\x1b\\");
        };

        Screensaver.KeepaliveDue += OnScreensaverKeepalive;
        Loaded += OnLoadedOnce;
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(() => _canvas.Focus(), DispatcherPriority.Input); };
    }

    private bool _started;

    private void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        Dispatcher.BeginInvoke(() => { _canvas.ApplyGridSize(); _ = ConnectAsync(); }, DispatcherPriority.Loaded);
    }

    public TerminalCanvas Canvas => _canvas;
    public override bool CanReconnect => State == TabState.Disconnected;
    public override bool IsLive => State is TabState.Connected or TabState.Connecting;
    public IReadOnlyList<string> EventLog { get { lock (_eventLog) return _eventLog.ToList(); } }

    // ================================================================== connect

    private async Task ConnectAsync()
    {
        if (_shutdown || Profile == null) return;
        var settings = SettingsStore.Settings;
        _infoBar.Visibility = Visibility.Collapsed;
        State = TabState.Connecting;
        _canvas.InputInterceptor = null;
        _connectCts = new CancellationTokenSource();

        var backend = BackendFactory.Create(Profile, settings);
        _backend = backend;
        backend.DataReceived += OnData;
        backend.Closed += reason => Dispatcher.BeginInvoke(() => OnClosed(backend, reason));
        backend.LineDisciplineChanged += () => Dispatcher.BeginInvoke(UpdateLineDiscipline);

        OpenLog(settings);
        Status($"Connecting: {SessionProfile.ProtocolName(Profile.Protocol)} {Profile.DisplayTarget}");
        try
        {
            int cols, rows;
            lock (_emu.SyncRoot) { cols = _emu.Cols; rows = _emu.Rows; }
            var ct = _connectCts.Token;
            await Task.Run(() => backend.ConnectAsync(this, cols, rows, ct), ct);
            if (_shutdown || !ReferenceEquals(backend, _backend)) return;
            State = TabState.Connected;
            Status($"Connected: {backend.Description}");
            SettingsStore.NoteRecent(Profile);
            UpdateLineDiscipline();
            lock (_emu.SyncRoot) cols = _emu.Cols;
            backend.Resize(_canvas.Cols, _canvas.Rows, (int)(_canvas.ActualWidth), (int)(_canvas.ActualHeight));
            if (backend is SshBackend ssh && (Profile.OpenSftpBrowser ?? settings.Ssh.OpenSftpBrowser) && !settings.Ssh.NoShell)
                OpenSftpPanel(ssh.Credentials);
            StartLoginScript(backend);
            _canvas.Focus();
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            if (_shutdown) return;
            Display("\r\n\x1b[33mConnection cancelled.\x1b[0m\r\n");
            OnClosed(backend, "Cancelled");
        }
        catch (Exception ex)
        {
            if (_shutdown) return;
            var msg = FriendlyError(ex);
            Status($"Connection failed: {ex}");
            Display($"\r\n\x1b[31m{msg}\x1b[0m\r\n");
            OnClosed(backend, msg);
        }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        System.Net.Sockets.SocketException se => se.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused => "Network error: Connection refused",
            System.Net.Sockets.SocketError.HostNotFound => "Host does not exist",
            System.Net.Sockets.SocketError.TimedOut => "Network error: Connection timed out",
            System.Net.Sockets.SocketError.NetworkUnreachable => "Network error: Network is unreachable",
            _ => $"Network error: {se.Message}",
        },
        Renci.SshNet.Common.SshConnectionException sce when sce.Message.Contains("host key", StringComparison.OrdinalIgnoreCase) =>
            "Host key not accepted — connection abandoned.",
        Renci.SshNet.Common.SshAuthenticationException ae => $"Authentication failed: {ae.Message}",
        Renci.SshNet.Common.SshOperationTimeoutException => "Network error: Connection timed out",
        TimeoutException te => te.Message,
        _ => ex.Message,
    };

    private void OpenLog(AppSettings settings)
    {
        _logger?.Dispose();
        _logger = null;
        var mode = Profile!.LogMode ?? settings.Logging.Mode;
        if (mode == LogMode.Off) return;
        try
        {
            _logger = SessionLogger.Open(settings.Logging, mode, Profile, path =>
            {
                var dlg = new FluentDialog(Window.GetWindow(this), "Log file exists",
                    $"The session log file already exists:\n{path}\n\nOverwrite it, or append to it?", "Overwrite", "Append", "Don't log");
                return dlg.ShowDialog() switch { DialogResultKind.Primary => true, DialogResultKind.Secondary => false, _ => null };
            });
            if (_logger != null)
            {
                Status($"Logging to {_logger.Path}");
                if (mode == LogMode.Printable) _emu.PrintableOutput += LogPrintable;
            }
        }
        catch (Exception ex)
        {
            Display($"\x1b[33mCould not open the session log: {ex.Message}\x1b[0m\r\n");
        }
    }

    private void LogPrintable(string text) => _logger?.WriteText(text);

    private void OnData(byte[] buf, int n)
    {
        var logger = _logger;
        if (logger?.Mode == LogMode.AllOutput) logger.WriteRaw(buf.AsSpan(0, n));
        lock (_emu.SyncRoot) _emu.Feed(buf.AsSpan(0, n));
        if (Interlocked.Exchange(ref _outputPending, 1) == 0)
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _outputPending, 0);
                _canvas.OnOutput();
                if (!IsVisible && State == TabState.Connected) HasAlert = HasAlert || false;
            }, DispatcherPriority.Background);
    }

    private void OnClosed(ITerminalBackend backend, string? reason)
    {
        if (!ReferenceEquals(backend, _backend) || _shutdown) return;
        bool wasConnected = State == TabState.Connected;
        State = TabState.Disconnected;
        _logger?.Dispose();
        _emu.PrintableOutput -= LogPrintable;
        _logger = null;
        Status(reason == null ? "Session ended" : $"Session ended: {reason}");

        var rule = SettingsStore.Settings.Interface.CloseTabOnExit;
        if (rule == CloseOnExit.Always || (rule == CloseOnExit.OnCleanExit && reason == null && wasConnected))
        {
            RequestClose();
            return;
        }

        _infoText.Text = reason == null ? "The session ended." : reason;
        _infoBar.Visibility = Visibility.Visible;
        if (SettingsStore.Settings.Interface.ReconnectKey)
        {
            Display($"\r\n\x1b[2m[{(reason == null ? "Session ended" : "Disconnected")} — press R to reconnect, Esc to close]\x1b[0m\r\n");
            _canvas.InputInterceptor = text =>
            {
                if (text.Equals("r", StringComparison.OrdinalIgnoreCase) || text == "\r") Reconnect();
                else if (text == "\x1b") RequestClose();
            };
        }
    }

    public override void Reconnect()
    {
        if (_shutdown) return;
        _connectCts?.Cancel();
        var old = _backend;
        _backend = null;
        try { old?.Dispose(); } catch { }
        Display("\r\n");
        _ = ConnectAsync();
    }

    // ================================================================== login script + initial command

    private Queue<LoginStep>? _loginSteps;
    private readonly StringBuilder _loginSeen = new();
    private DispatcherTimer? _loginTimeout;

    /// <summary>Runs the session's login script (expect → send), then types the initial command.</summary>
    private void StartLoginScript(ITerminalBackend backend)
    {
        var steps = Profile!.LoginScript?.Where(s => !string.IsNullOrWhiteSpace(s.Expect)).ToList() ?? new();
        if (steps.Count == 0)
        {
            SendInitialCommand(backend);
            return;
        }
        _loginSteps = new Queue<LoginStep>(steps);
        _loginSeen.Clear();
        _emu.PrintableOutput += OnLoginOutput;
        _loginTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _loginTimeout.Tick += (_, _) =>
        {
            Status("Login script: gave up waiting for a prompt after 30 seconds");
            StopLoginScript();
        };
        _loginTimeout.Start();
        Status($"Login script started ({steps.Count} steps)");
        // The first prompt may already be on screen (it can arrive before the script starts listening).
        lock (_emu.SyncRoot)
        {
            var current = _emu.ScreenLine(_emu.CursorY).GetText(0, _emu.CursorX + 1);
            OnLoginOutput(current);
        }
    }

    private void OnLoginOutput(string text)
    {
        // Called on the network thread with the emulator lock held: only inspect and post.
        _loginSeen.Append(text);
        if (_loginSeen.Length > 1024) _loginSeen.Remove(0, _loginSeen.Length - 1024);
        var steps = _loginSteps;
        if (steps == null || steps.Count == 0) return;
        var step = steps.Peek();
        bool match;
        try { match = System.Text.RegularExpressions.Regex.IsMatch(_loginSeen.ToString().TrimEnd(' '), step.Expect, System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromMilliseconds(200)); }
        catch { match = _loginSeen.ToString().Contains(step.Expect, StringComparison.OrdinalIgnoreCase); }
        if (!match) return;
        steps.Dequeue();
        _loginSeen.Clear();
        bool last = steps.Count == 0;
        Dispatcher.BeginInvoke(() =>
        {
            var b = _backend;
            if (b == null) return;
            var creds = (b as SshBackend)?.Credentials;
            string user = creds?.Username ?? (string.IsNullOrEmpty(Profile!.Username) ? SettingsStore.Settings.Connection.AutoLoginUsername : Profile.Username);
            string? password = creds?.Password ?? SecretStore.Unprotect(Profile!.Password);
            if (step.Send.Contains("{password}") && password == null)
            {
                password = FluentDialog.PromptText(Window.GetWindow(this), "Login script", $"Password for {user}@{Profile!.Host}:", "", password: true);
                if (password == null) { StopLoginScript(); return; }
            }
            var send = step.Send.Replace("{username}", user).Replace("{password}", password ?? "");
            // Never log what a step sent when it carries the password, even if the step isn't marked
            // secret: Status goes to the event log and to FLUENTTY_LOG files. Security review L8.
            bool secret = step.Secret || step.Send.Contains("{password}", StringComparison.OrdinalIgnoreCase);
            Status(secret ? "Login script: sent secret" : $"Login script: sent \"{send}\"");
            b.Send(_encoding.GetBytes(send + "\r"));
            if (last)
            {
                StopLoginScript();
                SendInitialCommand(b);
            }
        });
    }

    private void StopLoginScript()
    {
        _emu.PrintableOutput -= OnLoginOutput;
        _loginSteps = null;
        _loginTimeout?.Stop();
        _loginTimeout = null;
    }

    /// <summary>
    /// Types the initial command(s) one line at a time. PuTTY runs its "Remote command" instead of a
    /// shell; SSH.NET can't combine exec with a pty, so FluenTTY types it (like PuTTY CM's post-login commands).
    /// </summary>
    private async void SendInitialCommand(ITerminalBackend backend)
    {
        var initial = Profile!.InitialCommand ?? SettingsStore.Settings.Ssh.InitialCommand;
        if (string.IsNullOrWhiteSpace(initial)) return;
        await Task.Delay(400);
        foreach (var line in initial.Replace("\r\n", "\n").Split('\n'))
        {
            if (!ReferenceEquals(backend, _backend) || State != TabState.Connected) return;
            backend.Send(_encoding.GetBytes(line + "\r"));
            await Task.Delay(300);
        }
    }

    // ================================================================== input / line discipline

    private readonly StringBuilder _lineBuf = new();
    private bool _echo, _edit;
    private bool _prompting;

    private void UpdateLineDiscipline()
    {
        var t = SettingsStore.Settings.Terminal;
        var b = _backend;
        if (b == null) return;
        _echo = t.LocalEcho == AutoOnOff.On || (t.LocalEcho == AutoOnOff.Auto && b.AutoLocalEcho);
        _edit = t.LocalLineEditing == AutoOnOff.On || (t.LocalLineEditing == AutoOnOff.Auto && b.AutoLocalEdit);
        if (_prompting || State != TabState.Connected) return;
        _canvas.InputInterceptor = _edit ? LineEdit : null;
    }

    private void SendFromUser(byte[] data)
    {
        var b = _backend;
        if (b == null || State != TabState.Connected) return;
        if (_echo) LocalEcho(_encoding.GetString(data));
        b.Send(data);
    }

    private void LocalEcho(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\r') sb.Append("\r\n");
            else if (ch == '\x7f' || ch == '\b') sb.Append("\b \b");
            else if (ch >= ' ' || ch == '\t') sb.Append(ch);
            else if (ch != '\x1b') sb.Append('^').Append((char)(ch + '@'));
        }
        Display(sb.ToString());
    }

    /// <summary>PuTTY's local line editing: the line is edited here and sent when Return is pressed.</summary>
    private void LineEdit(string text)
    {
        var b = _backend;
        if (b == null) return;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                    _lineBuf.Append('\r');
                    if (_echo) Display("\r\n");
                    b.Send(_encoding.GetBytes(_lineBuf.ToString()));
                    _lineBuf.Clear();
                    break;
                case '\b':
                case '\x7f':
                    if (_lineBuf.Length > 0)
                    {
                        _lineBuf.Length--;
                        if (_echo) Display("\b \b");
                    }
                    break;
                case '\x15': // ^U
                    if (_echo) Display(string.Concat(Enumerable.Repeat("\b \b", _lineBuf.Length)));
                    _lineBuf.Clear();
                    break;
                case '\x03':
                case '\x04':
                case '\x1b':
                    // Control keys flush the line and go straight through.
                    _lineBuf.Append(ch);
                    b.Send(_encoding.GetBytes(_lineBuf.ToString()));
                    _lineBuf.Clear();
                    break;
                default:
                    if (ch < ' ' && ch != '\t') break;
                    _lineBuf.Append(ch);
                    if (_echo) Display(ch.ToString());
                    break;
            }
        }
    }

    private bool ConfirmPaste(string text)
    {
        var sel = SettingsStore.Settings.Selection;
        bool multi = sel.WarnMultiLinePaste && text.TrimEnd('\r').Contains('\r');
        bool large = sel.WarnLargePaste && text.Length > 5 * 1024;
        if (!multi && !large) return true;
        var preview = text.Length > 600 ? text[..600] + "…" : text;
        var box = new TextBox
        {
            Text = preview.Replace("\r", "\r\n"), IsReadOnly = true, MaxHeight = 220, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        };
        var panel = new StackPanel();
        var msg = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = multi
                ? $"You're about to paste {text.Split('\r').Length} lines. Each line may run as a command."
                : $"You're about to paste {text.Length:N0} characters.",
        };
        msg.SetResourceReference(StyleProperty, "Body");
        panel.Children.Add(msg);
        panel.Children.Add(box);
        return new FluentDialog(Window.GetWindow(this), "Paste into the terminal?", panel, "Paste").ShowDialog() == DialogResultKind.Primary;
    }

    // ================================================================== IConnectionUi (called from worker threads)

    public string? Prompt(string prompt, bool echo)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            if (_shutdown) { tcs.TrySetResult(null); return; }
            _prompting = true;
            Display(prompt);
            var sb = new StringBuilder();
            _canvas.InputInterceptor = text =>
            {
                foreach (var ch in text)
                {
                    if (ch == '\r')
                    {
                        Display("\r\n");
                        Finish(sb.ToString());
                        return;
                    }
                    if (ch is '\x03' or '\x04' or '\x1b')
                    {
                        Display("\r\n");
                        Finish(null);
                        return;
                    }
                    if (ch is '\b' or '\x7f')
                    {
                        if (sb.Length > 0) { sb.Length--; if (echo) Display("\b \b"); }
                        continue;
                    }
                    if (ch == '\x15')
                    {
                        if (echo) Display(string.Concat(Enumerable.Repeat("\b \b", sb.Length)));
                        sb.Clear();
                        continue;
                    }
                    if (ch < ' ') continue;
                    sb.Append(ch);
                    if (echo) Display(ch.ToString());
                }
            };
            _canvas.Focus();

            void Finish(string? value)
            {
                _canvas.InputInterceptor = null;
                _prompting = false;
                tcs.TrySetResult(value);
            }
            _promptCancel = () => Finish(null);
        });
        var result = tcs.Task.GetAwaiter().GetResult();
        _promptCancel = null;
        return result;
    }

    private Action? _promptCancel;

    public bool? ConfirmHostKey(HostKeyInfo info) => Dispatcher.Invoke(() =>
    {
        if (_shutdown) return (bool?)false;
        return HostKeyDialog.Show(Window.GetWindow(this), info);
    });

    public bool Confirm(string title, string message, string yes, string no) =>
        Dispatcher.Invoke(() => FluentDialog.Confirm(Window.GetWindow(this), title, message, yes, no));

    public void Status(string message)
    {
        lock (_eventLog) _eventLog.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{message}");
        Diagnostics.Log($"[{Title}] {message}");
    }

    public void Display(string text)
    {
        lock (_emu.SyncRoot) _emu.FeedText(text);
        if (Dispatcher.CheckAccess()) _canvas.OnOutput();
        else Dispatcher.BeginInvoke(() => _canvas.OnOutput());
    }

    // ================================================================== bell / title / scrollbar

    private readonly Queue<DateTime> _bells = new();
    private DateTime _bellSilencedUntil;

    private void OnBell()
    {
        var b = SettingsStore.Settings.Bell;
        var now = DateTime.Now;
        if (b.OverloadProtection)
        {
            if (now < _bellSilencedUntil) { _bellSilencedUntil = now.AddSeconds(b.OverloadSilenceSeconds); return; }
            _bells.Enqueue(now);
            while (_bells.Count > 0 && (now - _bells.Peek()).TotalSeconds > b.OverloadSeconds) _bells.Dequeue();
            if (_bells.Count >= b.OverloadCount)
            {
                _bellSilencedUntil = now.AddSeconds(b.OverloadSilenceSeconds);
                _bells.Clear();
                return;
            }
        }
        switch (b.Style)
        {
            case BellStyle.SystemDefault: SystemSounds.Asterisk.Play(); break;
            case BellStyle.Beep: MessageBeep(0xFFFFFFFF); break;
            case BellStyle.WaveFile:
                try { if (File.Exists(b.WaveFile)) new SoundPlayer(b.WaveFile).Play(); else SystemSounds.Asterisk.Play(); } catch { }
                break;
            case BellStyle.Visual:
                var anim = new DoubleAnimation(0.22, 0, TimeSpan.FromMilliseconds(220));
                _bellFlash.BeginAnimation(OpacityProperty, anim);
                break;
        }
        var window = Window.GetWindow(this);
        if (b.TabIndicator && (!IsVisible || window?.IsActive != true)) HasAlert = true;
        if (window != null && !window.IsActive && b.Taskbar != TaskbarBell.Disabled) Flash(window, b.Taskbar == TaskbarBell.Steady);
        Host?.Attention(this);
    }

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);

    private static void Flash(Window w, bool steady)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(w).Handle,
            dwFlags = steady ? 0x2u | 0xCu : 0x2u | 0xCu, // FLASHW_TRAY | FLASHW_TIMERNOFG
            uCount = steady ? 0u : 5u,
        };
        FlashWindowEx(ref info);
    }

    private void UpdateTitle()
    {
        var mode = SettingsStore.Settings.Interface.TabTitle;
        var p = Profile!;
        string baseName = string.IsNullOrEmpty(p.Name) ? p.DisplayTarget : p.Name;
        Title = mode switch
        {
            TabTitleSource.RemoteTitle when !string.IsNullOrWhiteSpace(_remoteTitle) => _remoteTitle,
            TabTitleSource.HostName => p.Host,
            _ => baseName,
        };
    }

    private void UpdateScrollBar()
    {
        int max = _canvas.MaxScrollOffset;
        bool alt;
        lock (_emu.SyncRoot) alt = _emu.AltScreenActive;
        bool show = SettingsStore.Settings.Appearance.ShowScrollbar && max > 0 && !alt;
        if (Window.GetWindow(this) is { WindowState: WindowState.Maximized, WindowStyle: WindowStyle.None } && !SettingsStore.Settings.Appearance.ScrollbarInFullScreen)
            show = false;
        _scrollBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _scrollBar.Maximum = max;
        _scrollBar.ViewportSize = Math.Max(1, _canvas.Rows);
        _scrollBar.LargeChange = Math.Max(1, _canvas.Rows - 1);
        _scrollBar.Value = max - _canvas.ScrollOffset;
    }

    // ================================================================== SFTP side panel

    private void OpenSftpPanel(SshCredentials creds)
    {
        if (_sftp != null || Profile == null) return;
        var fs = new SftpFileSystem(Profile, SettingsStore.Settings, creds, this);
        _sftp = new FileBrowserView(fs, compact: true);
        _sftp.Margin = new Thickness(0, 0, 0, 0);
        Grid.SetColumn(_sftp, 0);
        _root.Children.Add(_sftp);
        _sideColumn.Width = new GridLength(300);
        _sideColumn.MinWidth = 180;
        _splitter.Visibility = Visibility.Visible;
        _ = _sftp.StartAsync();
    }

    private void ToggleSftpPanel()
    {
        if (_sftp == null)
        {
            if (_backend is SshBackend ssh && State == TabState.Connected) OpenSftpPanel(ssh.Credentials);
            return;
        }
        bool visible = _sftp.Visibility == Visibility.Visible;
        _sftp.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        _splitter.Visibility = _sftp.Visibility;
        _sideColumn.Width = visible ? new GridLength(0) : new GridLength(300);
        _sideColumn.MinWidth = visible ? 0 : 180;
    }

    private void OnRemoteDirectory(string dir)
    {
        if (_sftp != null && SettingsStore.Settings.Ssh.SftpFollowTerminalFolder) _sftp.FollowDirectory(dir);
    }

    // ================================================================== menus

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in MenuItems()) menu.Items.Add(item);
        menu.PlacementTarget = _canvas;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Copy", "", () => _canvas.CopySelection(), _canvas.HasSelection, "Ctrl+Shift+C");
        yield return Item("Paste", "", () => _canvas.Paste(), true, "Ctrl+Shift+V");
        yield return Item("Select all", "", () => _canvas.SelectAll());
        yield return Item("Copy all to clipboard", "", () => TerminalCanvas.TrySetClipboard(_canvas.GetAllText()));
        yield return new Separator();
        if (_backend != null && _backend.SpecialCommands.Count > 0)
        {
            var specials = new MenuItem { Header = "Special commands" };
            foreach (var sc in _backend.SpecialCommands)
            {
                var cmd = sc;
                var mi = new MenuItem { Header = cmd.Title };
                mi.Click += (_, _) => _backend?.SendSpecial(cmd);
                specials.Items.Add(mi);
            }
            yield return specials;
        }
        if (Profile?.Protocol == ProtocolKind.Ssh)
            yield return Item(_sftp?.Visibility == Visibility.Visible ? "Hide SFTP browser" : "Show SFTP browser", "", ToggleSftpPanel, State == TabState.Connected || _sftp != null);
        yield return Item("Event log", "", () => EventLogWindow.ShowFor(Window.GetWindow(this), Title, EventLog));
        yield return Item("Clear scrollback", "", () => { lock (_emu.SyncRoot) _emu.ClearScrollback(); _canvas.OnOutput(); });
        yield return Item("Reset terminal", "", () => { lock (_emu.SyncRoot) _emu.FullReset(); _canvas.OnOutput(); });
        yield return Item("Reset zoom", "", () => { _zoom = 0; ApplySettings(); }, _zoom != 0);
        yield return new Separator();
        yield return Item("Reconnect", "", Reconnect, State == TabState.Disconnected);
    }

    // ================================================================== lifecycle

    public override void OnActivated()
    {
        HasAlert = false;
        Dispatcher.BeginInvoke(() => _canvas.Focus(), DispatcherPriority.Input);
    }

    public override void ApplySettings()
    {
        var s = SettingsStore.Settings;
        _encoding = TerminalConfig.GetEncoding(Profile!.RemoteCharset ?? s.Translation.RemoteCharset);
        lock (_emu.SyncRoot) _emu.ApplyOptions(TerminalConfig.BuildOptions(s, Profile));
        _canvas.SetLook(TerminalConfig.BuildLook(s, Profile, ThemeService.IsDark, _zoom), s);
        UpdateTitle();
        UpdateScrollBar();
        UpdateLineDiscipline();
        _sftp?.ApplySettings();
    }

    /// <summary>The crab screensaver is running: send the protocol's no-op so an idle connection isn't dropped.</summary>
    private void OnScreensaverKeepalive()
    {
        var b = _backend;
        if (b == null || State != TabState.Connected) return;
        Task.Run(() =>
        {
            try { if (b.SendKeepalive()) Diagnostics.Log($"[{Title}] screensaver keepalive sent"); }
            catch (Exception ex) { Diagnostics.Log($"[{Title}] screensaver keepalive failed: {ex.Message}"); }
        });
    }

    public override void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        Screensaver.KeepaliveDue -= OnScreensaverKeepalive;
        _promptCancel?.Invoke();
        _connectCts?.Cancel();
        var b = _backend;
        _backend = null;
        Task.Run(() => { try { b?.Dispose(); } catch { } });
        _logger?.Dispose();
        _logger = null;
        _sftp?.Shutdown();
    }
}
