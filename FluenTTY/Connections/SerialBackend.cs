using System.IO.Ports;
using FluenTTY.Models;

namespace FluenTTY.Connections;

/// <summary>COM port session (PuTTY's Connection › Serial: line, speed, data/stop bits, parity, flow control).</summary>
public sealed class SerialBackend : ITerminalBackend
{
    private readonly SessionProfile _profile;
    private readonly AppSettings _settings;
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private int _closed;
    private readonly object _sendLock = new();

    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Closed;
    public event Action? LineDisciplineChanged { add { } remove { } }

    public SerialBackend(SessionProfile profile, AppSettings settings)
    {
        _profile = profile;
        _settings = settings;
    }

    public bool IsConnected => _port?.IsOpen == true && _closed == 0;
    public bool AutoLocalEcho => false;
    public bool AutoLocalEdit => false;

    public string PortName => string.IsNullOrWhiteSpace(_profile.Host) ? _settings.Serial.Line : _profile.Host.Trim();
    public int Speed => _profile.SerialSpeed ?? _settings.Serial.Speed;
    public int DataBits => _profile.SerialDataBits ?? _settings.Serial.DataBits;
    public SerialStopBits Stop => _profile.SerialStopBits ?? _settings.Serial.StopBits;
    public SerialParity ParityMode => _profile.SerialParity ?? _settings.Serial.Parity;
    public SerialFlow Flow => _profile.SerialFlow ?? _settings.Serial.FlowControl;

    public string Description =>
        $"{PortName} {Speed} {DataBits}{ParityMode.ToString()[0]}{(Stop == SerialStopBits.One ? "1" : Stop == SerialStopBits.Two ? "2" : "1.5")} flow {Flow}";

    public Task ConnectAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct)
    {
        ui.Status($"Opening {Description}");
        var port = new SerialPort(PortName, Speed)
        {
            DataBits = DataBits,
            StopBits = Stop switch { SerialStopBits.Two => StopBits.Two, SerialStopBits.OnePointFive => StopBits.OnePointFive, _ => StopBits.One },
            Parity = ParityMode switch
            {
                SerialParity.Odd => Parity.Odd,
                SerialParity.Even => Parity.Even,
                SerialParity.Mark => Parity.Mark,
                SerialParity.Space => Parity.Space,
                _ => Parity.None,
            },
            Handshake = Flow switch
            {
                SerialFlow.XonXoff => Handshake.XOnXOff,
                SerialFlow.RtsCts => Handshake.RequestToSend,
                _ => Handshake.None,
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        if (Flow == SerialFlow.DsrDtr)
        {
            // .NET has no DSR/DTR handshake mode; hold DTR up and let the device pace us.
            port.DtrEnable = true;
        }
        else
        {
            port.DtrEnable = _settings.Serial.DtrEnable;
            if (Flow != SerialFlow.RtsCts) port.RtsEnable = _settings.Serial.RtsEnable;
        }
        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            port.Dispose();
            throw new IOException($"{PortName} is in use by another program.");
        }
        catch (FileNotFoundException)
        {
            port.Dispose();
            throw new IOException($"{PortName} doesn't exist. Available ports: {string.Join(", ", SerialPort.GetPortNames().DefaultIfEmpty("none"))}");
        }
        _port = port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_cts.Token));
        ui.Status($"Opened {PortName}");
        return Task.CompletedTask;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        string? reason = null;
        try
        {
            var stream = _port!.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                DataReceived?.Invoke(buf, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            reason = $"Serial line error: {ex.Message}";
        }
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(reason);
    }

    public void Send(byte[] data)
    {
        var port = _port;
        if (port == null || !port.IsOpen) return;
        try
        {
            lock (_sendLock) port.BaseStream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke($"Serial line error: {ex.Message}");
        }
    }

    public void Resize(int cols, int rows, int pixelWidth, int pixelHeight) { }

    private static readonly SpecialCommand[] Specials = { new("brk", "Break"), new("xon", "Send XON"), new("xoff", "Send XOFF") };
    public IReadOnlyList<SpecialCommand> SpecialCommands => Specials;

    /// <summary>A serial line has no idle timeout and no in-band no-op.</summary>
    public bool SendKeepalive() => false;

    public void SendSpecial(SpecialCommand command)
    {
        var port = _port;
        if (port == null || !port.IsOpen) return;
        switch (command.Id)
        {
            case "brk":
                Task.Run(async () =>
                {
                    try
                    {
                        port.BreakState = true;
                        await Task.Delay(Math.Clamp(_settings.Serial.BreakMilliseconds, 10, 5000));
                        port.BreakState = false;
                    }
                    catch { }
                });
                break;
            case "xon": Send(new byte[] { 0x11 }); break;
            case "xoff": Send(new byte[] { 0x13 }); break;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _port?.Close(); } catch { }
        _port?.Dispose();
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(null);
    }
}
