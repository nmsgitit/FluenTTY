using FluenTTY.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FluenTTY.Connections;

/// <summary>SSH-2 terminal session over SSH.NET: shell with a pty, port forwarding, keepalives.</summary>
public sealed class SshBackend : ITerminalBackend
{
    private readonly SessionProfile _profile;
    private readonly AppSettings _settings;
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closed;
    private readonly object _sendLock = new();
    private readonly List<ForwardedPort> _forwards = new();

    public SshCredentials Credentials { get; } = new();

    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Closed;
    public event Action? LineDisciplineChanged { add { } remove { } }

    public SshBackend(SessionProfile profile, AppSettings settings)
    {
        _profile = profile;
        _settings = settings;
    }

    public bool IsConnected => _client?.IsConnected == true && _closed == 0;
    public bool AutoLocalEcho => false;
    public bool AutoLocalEdit => false;
    public IReadOnlyList<SpecialCommand> SpecialCommands => Array.Empty<SpecialCommand>();
    public void SendSpecial(SpecialCommand command) { }

    public bool SendKeepalive()
    {
        var client = _client;
        if (client == null || !IsConnected) return false;
        try
        {
            // Obsolete only in favour of KeepAliveInterval (a timer, which stays the user's ping_interval setting);
            // this is a one-off SSH_MSG_IGNORE.
#pragma warning disable CS0618
            client.SendKeepAlive();
#pragma warning restore CS0618
            return true;
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
    public string Description { get; private set; } = "SSH";

    public Task ConnectAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct) => Task.Run(() =>
    {
        var client = SshConnector.Connect(info => new SshClient(info), _profile, _settings, ui, Credentials, ct);
        _client = client;
        Description = $"SSH {Credentials.Username}@{_profile.Host}:{_profile.EffectivePort} ({client.ConnectionInfo.CurrentClientEncryption})";

        int keepalive = _profile.KeepaliveSeconds ?? _settings.Connection.KeepaliveSeconds;
        if (keepalive > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(keepalive);
        client.ErrorOccurred += (_, e) => RaiseClosed($"Network error: {e.Exception.Message}");

        StartForwards(client, ui);

        if (_settings.Ssh.NoShell)
        {
            ui.Display("Connected without a shell (Settings › SSH › \"Don't start a shell\"). Port forwards stay open while this tab is open.\r\n");
            return;
        }

        if (_settings.Ssh.NoPty)
        {
            _shell = client.CreateShellStreamNoTerminal(65536);
        }
        else
        {
            var modes = new Dictionary<TerminalModes, uint>
            {
                [TerminalModes.VERASE] = _settings.Keyboard.Backspace == BackspaceKey.ControlH ? 8u : 127u,
            };
            var charset = _profile.RemoteCharset ?? _settings.Translation.RemoteCharset;
            if (charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)) modes[TerminalModes.IUTF8] = 1;
            string term = _profile.TerminalType ?? _settings.Connection.TerminalType;
            _shell = client.CreateShellStream(term, (uint)cols, (uint)rows, 0, 0, 65536, modes);
        }
        _shell.Closed += (_, _) => RaiseClosed(null);
        _shell.ErrorOccurred += (_, e) => RaiseClosed($"Channel error: {e.Exception.Message}");

        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "SSH reader" };
        thread.Start();

    }, ct);

    private void StartForwards(SshClient client, IConnectionUi ui)
    {
        var list = _settings.Ssh.PortForwards.Concat(_profile.PortForwards).ToList();
        foreach (var f in list)
        {
            try
            {
                ForwardedPort port;
                switch (f.Kind)
                {
                    case PortForwardKind.Local:
                    {
                        string bind = f.BindAddress.Length > 0 ? f.BindAddress : _settings.Ssh.LocalPortsAcceptAll ? "0.0.0.0" : "127.0.0.1";
                        port = new ForwardedPortLocal(bind, (uint)f.SourcePort, f.DestinationHost, (uint)f.DestinationPort);
                        break;
                    }
                    case PortForwardKind.Remote:
                    {
                        string bind = f.BindAddress.Length > 0 ? f.BindAddress : _settings.Ssh.RemotePortsAcceptAll ? "0.0.0.0" : "localhost";
                        port = new ForwardedPortRemote(bind, (uint)f.SourcePort, f.DestinationHost, (uint)f.DestinationPort);
                        break;
                    }
                    default:
                    {
                        string bind = f.BindAddress.Length > 0 ? f.BindAddress : _settings.Ssh.LocalPortsAcceptAll ? "0.0.0.0" : "127.0.0.1";
                        port = new ForwardedPortDynamic(bind, (uint)f.SourcePort);
                        break;
                    }
                }
                port.Exception += (_, e) => ui.Status($"Port forwarding {f}: {e.Exception.Message}");
                client.AddForwardedPort(port);
                port.Start();
                _forwards.Add(port);
                ui.Status($"Port forwarding started: {f}");
            }
            catch (Exception ex)
            {
                ui.Display($"\x1b[33mPort forwarding {f} failed: {ex.Message}\x1b[0m\r\n");
            }
        }
    }

    public IReadOnlyList<ForwardedPort> Forwards => _forwards;

    private void ReadLoop()
    {
        var buf = new byte[32768];
        try
        {
            while (true)
            {
                var shell = _shell;
                if (shell == null) break;
                int n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf, n);
            }
            RaiseClosed(null);
        }
        catch (ObjectDisposedException) { RaiseClosed(null); }
        catch (Exception ex) { RaiseClosed($"Connection lost: {ex.Message}"); }
    }

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    public void Send(byte[] data)
    {
        var shell = _shell;
        if (shell == null || _closed != 0) return;
        try
        {
            lock (_sendLock)
            {
                shell.Write(data, 0, data.Length);
                shell.Flush();
            }
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            RaiseClosed($"Connection lost: {ex.Message}");
        }
    }

    public void Resize(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        try { _shell?.ChangeWindowSize((uint)cols, (uint)rows, (uint)Math.Max(0, pixelWidth), (uint)Math.Max(0, pixelHeight)); }
        catch { }
    }

    public void Dispose()
    {
        foreach (var f in _forwards) { try { f.Stop(); } catch { } }
        try { _shell?.Dispose(); } catch { }
        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
        _shell = null;
        RaiseClosed(null);
    }
}
