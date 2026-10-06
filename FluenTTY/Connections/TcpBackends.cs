using System.Net.Sockets;
using System.Text;
using FluenTTY.Models;

namespace FluenTTY.Connections;

/// <summary>Shared socket plumbing: a read loop that raises DataReceived and a thread-safe send.</summary>
public abstract class TcpBackendBase : ITerminalBackend
{
    protected Socket? Socket;
    protected readonly SessionProfile Profile;
    protected readonly AppSettings Settings;
    private readonly object _sendLock = new();
    private CancellationTokenSource? _cts;
    private int _closed;

    public event Action<byte[], int>? DataReceived;
    public event Action<string?>? Closed;
    public event Action? LineDisciplineChanged;

    protected TcpBackendBase(SessionProfile profile, AppSettings settings)
    {
        Profile = profile;
        Settings = settings;
    }

    public bool IsConnected => Socket?.Connected == true && _closed == 0;
    public virtual bool AutoLocalEcho => false;
    public virtual bool AutoLocalEdit => false;
    public virtual IReadOnlyList<SpecialCommand> SpecialCommands => Array.Empty<SpecialCommand>();
    public virtual void SendSpecial(SpecialCommand command) { }
    public virtual bool SendKeepalive() => false;
    public virtual string Description => $"{SessionProfile.ProtocolName(Profile.Protocol)} {Profile.Host}:{Profile.EffectivePort}";

    protected virtual int LocalPort => 0;

    public async Task ConnectAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct)
    {
        Socket = await SocketConnector.ConnectAsync(Profile.Host, Profile.EffectivePort, Settings, Profile.UseProxy, ct, LocalPort, ui);
        ui.Status($"Connected to {Profile.Host}:{Profile.EffectivePort}");
        _cts = new CancellationTokenSource();
        await OnConnectedAsync(ui, cols, rows, ct);
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    protected virtual Task OnConnectedAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct) => Task.CompletedTask;

    private async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[32768];
        string? reason = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await Socket!.ReceiveAsync(buf, SocketFlags.None, ct);
                if (n <= 0) break;
                OnReceive(buf, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            reason = ex is SocketException se ? $"Network error: {se.Message}" : ex.Message;
        }
        RaiseClosed(reason);
    }

    /// <summary>Protocol decoding hook; the default passes data straight through.</summary>
    protected virtual void OnReceive(byte[] buf, int n) => Deliver(buf, n);

    protected void Deliver(byte[] buf, int n)
    {
        if (n > 0) DataReceived?.Invoke(buf, n);
    }

    protected void RaiseLineDisciplineChanged() => LineDisciplineChanged?.Invoke();

    protected void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    public virtual void Send(byte[] data) => SendRaw(data);

    protected void SendRaw(byte[] data)
    {
        var s = Socket;
        if (s == null || _closed != 0) return;
        try
        {
            lock (_sendLock)
            {
                int sent = 0;
                while (sent < data.Length) sent += s.Send(data, sent, data.Length - sent, SocketFlags.None);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            RaiseClosed($"Network error: {ex.Message}");
        }
    }

    public virtual void Resize(int cols, int rows, int pixelWidth, int pixelHeight) { }

    public virtual void Dispose()
    {
        _cts?.Cancel();
        try { Socket?.Shutdown(SocketShutdown.Both); } catch { }
        Socket?.Dispose();
        RaiseClosed(null);
    }
}

/// <summary>Raw TCP: bytes both ways, nothing else (PuTTY's "Raw" protocol).</summary>
public sealed class RawBackend : TcpBackendBase
{
    public RawBackend(SessionProfile p, AppSettings s) : base(p, s) { }
    // PuTTY's raw backend turns local echo and line editing on in "Auto".
    public override bool AutoLocalEcho => true;
    public override bool AutoLocalEdit => true;
}

/// <summary>
/// Telnet (RFC 854) with the option set PuTTY negotiates (telnet.c): ECHO, SGA, NAWS, TTYPE, TSPEED,
/// NEW-ENVIRON / old ENVIRON, BINARY.
/// </summary>
public sealed class TelnetBackend : TcpBackendBase
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, GA = 249, EL = 248, EC = 247,
        AYT = 246, AO = 245, IP = 244, BRK = 243, DM = 242, NOP = 241, SE = 240, EOR = 239, ABORT = 238, SUSP = 237, xEOF = 236;
    private const byte OptBinary = 0, OptEcho = 1, OptSga = 3, OptTtype = 24, OptNaws = 31, OptTspeed = 32, OptOldEnv = 36, OptNewEnv = 39;

    private readonly HashSet<byte> _weWill = new();   // options we have agreed to perform
    private readonly HashSet<byte> _theyWill = new(); // options the server performs
    private int _cols = 80, _rows = 24;
    private readonly string _termType;
    private readonly TelnetSettings _tn;

    public TelnetBackend(SessionProfile p, AppSettings s) : base(p, s)
    {
        _tn = s.Telnet;
        _termType = p.TerminalType ?? s.Connection.TerminalType;
    }

    public override bool AutoLocalEcho => !_theyWill.Contains(OptEcho);
    public override bool AutoLocalEdit => !(_theyWill.Contains(OptSga) && _weWill.Contains(OptSga));

    protected override Task OnConnectedAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct)
    {
        _cols = cols; _rows = rows;
        if (!_tn.PassiveNegotiation)
        {
            var init = new List<byte>();
            void Add(byte verb, byte opt) { init.Add(IAC); init.Add(verb); init.Add(opt); }
            Add(WILL, OptNaws); _weWill.Add(OptNaws);
            Add(WILL, OptTspeed); _weWill.Add(OptTspeed);
            Add(WILL, OptTtype); _weWill.Add(OptTtype);
            Add(WILL, OptNewEnv); _weWill.Add(OptNewEnv);
            Add(DO, OptEcho);
            Add(WILL, OptSga); _weWill.Add(OptSga);
            Add(DO, OptSga);
            SendRaw(init.ToArray());
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ receive

    private enum TState { Data, Iac, Will, Wont, Do, Dont, Sb, SbData, SbIac, SeenCr }
    private TState _state = TState.Data;
    private byte _sbOpt;
    private readonly List<byte> _sb = new();

    protected override void OnReceive(byte[] buf, int n)
    {
        var outBuf = new byte[n];
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = buf[i];
            switch (_state)
            {
                case TState.Data:
                    if (c == IAC) _state = TState.Iac;
                    else
                    {
                        outBuf[o++] = c;
                        if (c == '\r' && !_theyWill.Contains(OptBinary)) _state = TState.SeenCr;
                    }
                    break;
                case TState.SeenCr:
                    // CR NUL means a bare CR; drop the NUL.
                    _state = TState.Data;
                    if (c == 0) break;
                    i--;
                    break;
                case TState.Iac:
                    _state = TState.Data;
                    switch (c)
                    {
                        case IAC: outBuf[o++] = IAC; break;
                        case WILL: _state = TState.Will; break;
                        case WONT: _state = TState.Wont; break;
                        case DO: _state = TState.Do; break;
                        case DONT: _state = TState.Dont; break;
                        case SB: _state = TState.Sb; break;
                    }
                    break;
                case TState.Will: _state = TState.Data; OnWill(c); break;
                case TState.Wont: _state = TState.Data; OnWont(c); break;
                case TState.Do: _state = TState.Data; OnDo(c); break;
                case TState.Dont: _state = TState.Data; OnDont(c); break;
                case TState.Sb: _sbOpt = c; _sb.Clear(); _state = TState.SbData; break;
                case TState.SbData:
                    if (c == IAC) _state = TState.SbIac;
                    else if (_sb.Count < 4096) _sb.Add(c);
                    break;
                case TState.SbIac:
                    if (c == SE) { _state = TState.Data; OnSubnegotiation(); }
                    else { if (c == IAC && _sb.Count < 4096) _sb.Add(IAC); _state = TState.SbData; }
                    break;
            }
        }
        Deliver(outBuf, o);
    }

    private void Reply(byte verb, byte opt) => SendRaw(new[] { IAC, verb, opt });

    private void OnWill(byte opt)
    {
        if (opt is OptEcho or OptSga or OptBinary)
        {
            if (_theyWill.Add(opt)) Reply(DO, opt);
            RaiseLineDisciplineChanged();
        }
        else Reply(DONT, opt);
    }

    private void OnWont(byte opt)
    {
        if (_theyWill.Remove(opt)) Reply(DONT, opt);
        RaiseLineDisciplineChanged();
    }

    private void OnDo(byte opt)
    {
        if (opt is OptNaws or OptTspeed or OptTtype or OptNewEnv or OptSga or OptBinary)
        {
            if (_weWill.Add(opt) || _tn.PassiveNegotiation) Reply(WILL, opt);
            if (opt == OptNaws) SendNaws();
            RaiseLineDisciplineChanged();
        }
        else Reply(WONT, opt);
    }

    private void OnDont(byte opt)
    {
        if (_weWill.Remove(opt)) Reply(WONT, opt);
        RaiseLineDisciplineChanged();
    }

    private void OnSubnegotiation()
    {
        if (_sb.Count < 1 || _sb[0] != 1) return; // SEND
        var reply = new List<byte> { IAC, SB, _sbOpt, 0 }; // IS
        switch (_sbOpt)
        {
            case OptTtype:
                reply.AddRange(Encoding.ASCII.GetBytes(_termType.ToUpperInvariant()));
                break;
            case OptTspeed:
                reply.AddRange(Encoding.ASCII.GetBytes(Settings.Connection.TerminalSpeed));
                break;
            case OptNewEnv:
            case OptOldEnv:
            {
                // RFC 1572: VAR=0, VALUE=1, USERVAR=3. (Old BSD servers swap VAR/VALUE; that's the "RFC" switch.)
                byte var = 0, value = 1;
                if (_sbOpt == OptOldEnv && !_tn.RfcEnvironment) { var = 1; value = 0; }
                string user = string.IsNullOrEmpty(Profile.Username) ? Settings.Connection.AutoLoginUsername : Profile.Username;
                if (!string.IsNullOrEmpty(user))
                {
                    reply.Add(var); reply.AddRange(Encoding.ASCII.GetBytes("USER"));
                    reply.Add(value); reply.AddRange(Encoding.UTF8.GetBytes(user));
                }
                foreach (var ev in Settings.Connection.Environment)
                {
                    reply.Add(3); reply.AddRange(Encoding.UTF8.GetBytes(ev.Name));
                    reply.Add(value); reply.AddRange(Encoding.UTF8.GetBytes(ev.Value));
                }
                break;
            }
            default:
                return;
        }
        reply.Add(IAC); reply.Add(SE);
        SendRaw(reply.ToArray());
    }

    private void SendNaws()
    {
        if (!_weWill.Contains(OptNaws)) return;
        var b = new List<byte> { IAC, SB, OptNaws };
        foreach (var v in new[] { _cols >> 8, _cols & 255, _rows >> 8, _rows & 255 })
        {
            b.Add((byte)v);
            if (v == IAC) b.Add(IAC);
        }
        b.Add(IAC); b.Add(SE);
        SendRaw(b.ToArray());
    }

    public override void Resize(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        _cols = cols; _rows = rows;
        SendNaws();
    }

    // ------------------------------------------------------------------ send

    public override void Send(byte[] data)
    {
        var o = new List<byte>(data.Length + 8);
        foreach (var c in data)
        {
            if (c == IAC) { o.Add(IAC); o.Add(IAC); }
            else if (c == '\r' && _tn.ReturnSendsTelnetNewline) { o.Add((byte)'\r'); o.Add((byte)'\n'); }
            else if (c == '\r' && !_weWill.Contains(OptBinary)) { o.Add((byte)'\r'); o.Add(0); }
            else if (_tn.KeyboardSendsTelnetCommands && c == 0x7F) { o.Add(IAC); o.Add(EC); }
            else if (_tn.KeyboardSendsTelnetCommands && c == 0x03) { o.Add(IAC); o.Add(IP); }
            else if (_tn.KeyboardSendsTelnetCommands && c == 0x1A) { o.Add(IAC); o.Add(SUSP); }
            else o.Add(c);
        }
        SendRaw(o.ToArray());
    }

    private static readonly SpecialCommand[] Specials =
    {
        new("brk", "Break"), new("ayt", "Are You There"), new("synch", "Synch"), new("ec", "Erase Character"),
        new("el", "Erase Line"), new("ga", "Go Ahead"), new("nop", "No Operation"), new("abort", "Abort Process"),
        new("ao", "Abort Output"), new("ip", "Interrupt Process"), new("susp", "Suspend Process"),
        new("eor", "End Of Record"), new("eof", "End Of File"),
    };

    public override IReadOnlyList<SpecialCommand> SpecialCommands => Specials;

    public override void SendSpecial(SpecialCommand command)
    {
        byte code = command.Id switch
        {
            "brk" => BRK, "ayt" => AYT, "ec" => EC, "el" => EL, "ga" => GA, "nop" => NOP, "abort" => ABORT,
            "ao" => AO, "ip" => IP, "susp" => SUSP, "eor" => EOR, "eof" => xEOF, "synch" => DM, _ => NOP,
        };
        if (command.Id == "synch")
        {
            // Synch = IAC DM sent as urgent data (PuTTY sends the IAC as OOB).
            try { Socket?.Send(new[] { IAC }, SocketFlags.OutOfBand); } catch { }
            SendRaw(new[] { DM });
            return;
        }
        SendRaw(new[] { IAC, code });
    }

    public override bool SendKeepalive()
    {
        if (!IsConnected) return false;
        SendRaw(new[] { IAC, NOP });
        return true;
    }

    public override string Description => $"Telnet {Profile.Host}:{Profile.EffectivePort}";
}

/// <summary>Rlogin (RFC 1282) — PuTTY's rlogin.c.</summary>
public sealed class RloginBackend : TcpBackendBase
{
    private bool _gotAck;
    private int _cols = 80, _rows = 24, _pw, _ph;

    public RloginBackend(SessionProfile p, AppSettings s) : base(p, s) { }

    protected override int LocalPort => 1023;

    protected override Task OnConnectedAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct)
    {
        _cols = cols; _rows = rows;
        string local = string.IsNullOrEmpty(Settings.Rlogin.LocalUsername) ? Environment.UserName : Settings.Rlogin.LocalUsername;
        string remote = Profile.Username;
        if (string.IsNullOrEmpty(remote)) remote = Settings.Connection.AutoLoginUsername;
        if (string.IsNullOrEmpty(remote)) remote = ui.Prompt("login as: ", true) ?? throw new OperationCanceledException();
        string term = (Profile.TerminalType ?? Settings.Connection.TerminalType) + "/" + Settings.Connection.TerminalSpeed.Split(',')[0];
        SendRaw(Encoding.UTF8.GetBytes($"\0{local}\0{remote}\0{term}\0"));
        return Task.CompletedTask;
    }

    protected override void OnReceive(byte[] buf, int n)
    {
        int start = 0;
        if (!_gotAck && n > 0)
        {
            _gotAck = true;
            if (buf[0] == 0) start = 1;
            SendWindowSize();
        }
        if (n - start > 0)
        {
            var copy = new byte[n - start];
            Array.Copy(buf, start, copy, 0, copy.Length);
            Deliver(copy, copy.Length);
        }
    }

    private void SendWindowSize()
    {
        if (!_gotAck) return;
        SendRaw(new byte[] { 0xFF, 0xFF, (byte)'s', (byte)'s', (byte)(_rows >> 8), (byte)_rows, (byte)(_cols >> 8), (byte)_cols,
            (byte)(_pw >> 8), (byte)_pw, (byte)(_ph >> 8), (byte)_ph });
    }

    public override void Resize(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        _cols = cols; _rows = rows; _pw = pixelWidth; _ph = pixelHeight;
        SendWindowSize();
    }
}

/// <summary>Rsh (BSD remote shell, port 514): runs one command; its output appears in the terminal.</summary>
public sealed class RshBackend : TcpBackendBase
{
    private bool _gotAck;
    private string _command = "";

    public RshBackend(SessionProfile p, AppSettings s) : base(p, s) { }

    protected override int LocalPort => 1023;
    public override bool AutoLocalEcho => true;
    public override bool AutoLocalEdit => true;

    protected override Task OnConnectedAsync(IConnectionUi ui, int cols, int rows, CancellationToken ct)
    {
        string local = string.IsNullOrEmpty(Settings.Rlogin.LocalUsername) ? Environment.UserName : Settings.Rlogin.LocalUsername;
        string remote = string.IsNullOrEmpty(Profile.Username) ? Settings.Connection.AutoLoginUsername : Profile.Username;
        if (string.IsNullOrEmpty(remote)) remote = ui.Prompt("login as: ", true) ?? throw new OperationCanceledException();
        _command = Profile.RshCommand ?? Settings.Rlogin.RshCommand;
        if (string.IsNullOrWhiteSpace(_command)) _command = ui.Prompt("command: ", true) ?? throw new OperationCanceledException();
        // "0" = no separate stderr connection: stderr comes back on this socket too.
        SendRaw(Encoding.UTF8.GetBytes($"0\0{local}\0{remote}\0{_command}\0"));
        return Task.CompletedTask;
    }

    protected override void OnReceive(byte[] buf, int n)
    {
        int start = 0;
        if (!_gotAck && n > 0)
        {
            _gotAck = true;
            if (buf[0] == 0) start = 1;
            else if (buf[0] == 1)
            {
                // Error: the rest of the packet is the message.
                var msg = Encoding.UTF8.GetString(buf, 1, n - 1).Trim();
                RaiseClosed($"rsh refused: {msg}");
                return;
            }
        }
        var copy = new byte[n - start];
        Array.Copy(buf, start, copy, 0, copy.Length);
        // rsh output has bare LFs (no pty); make them display as new lines.
        var converted = LfToCrLf(copy);
        Deliver(converted, converted.Length);
    }

    private static byte[] LfToCrLf(byte[] data)
    {
        var o = new List<byte>(data.Length + 16);
        byte prev = 0;
        foreach (var b in data)
        {
            if (b == '\n' && prev != '\r') o.Add((byte)'\r');
            o.Add(b);
            prev = b;
        }
        return o.ToArray();
    }


    public override string Description => $"rsh {Profile.Host} \"{_command}\"";
}
