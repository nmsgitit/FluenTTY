using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using TestServers;

// FluenTTY sandbox servers (localhost only). User "test", password "test" everywhere.
//   SSH + SFTP 2222 · Telnet 2323 · Raw 2324 · FTP 2121 · VNC 5959 (5960 also offers "no authentication")
//   SFTP also serves a virtual hostile /evil tree (path traversal, rooted names, endless nesting).
// Usage: dotnet run --project tests\TestServers -- <sandbox folder>
var root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "FluenTTY-TestServers");
var files = Path.Combine(root, "files");
Directory.CreateDirectory(Path.Combine(files, "etc"));
Directory.CreateDirectory(Path.Combine(files, "home", "test"));
File.WriteAllText(Path.Combine(files, "etc", "motd"), "Welcome to the FluenTTY sandbox.\n");
File.WriteAllText(Path.Combine(files, "home", "test", "notes.txt"), "Edit me in FluenTTY's editor.\nLine two.\n");
File.WriteAllText(Path.Combine(files, "home", "test", "script.py"), "#!/usr/bin/env python3\nimport sys\n\ndef main():\n    print(\"hello\")  # comment\n\nif __name__ == \"__main__\":\n    main()\n");
File.WriteAllText(Path.Combine(files, "home", "test", "config.json"), "{\n  \"name\": \"switch-01\",\n  \"vlans\": [10, 20, 30]\n}\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

_ = Task.Run(() => RunTelnet(2323, files, ct));
_ = Task.Run(() => RunRaw(2324, ct));
_ = Task.Run(() => new FtpServer(files).ListenAsync(2121, ct));
_ = Task.Run(() => new VncServer().ListenAsync(5959, ct));
_ = Task.Run(() => new VncServer().ListenAsync(5960, ct, offerNone: true)); // security test: offers None + VNC auth
_ = Task.Run(() => RunSsh(2222, files, ct));
Console.WriteLine($"Test servers running (files: {files}). Ctrl+C to stop.");
try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }

// ---------------------------------------------------------------- SSH (Microsoft.DevTunnels.Ssh)

static async Task RunSsh(int port, string files, CancellationToken ct)
{
    try
    {
        var config = new SshSessionConfiguration();
        var hostKey = SshAlgorithms.PublicKey.ECDsaSha2Nistp384.GenerateKeyPair();
        var server = new Microsoft.DevTunnels.Ssh.Tcp.SshServer(config, new TraceSource("ssh-test"));
        server.Credentials = new SshServerCredentials(hostKey);
        server.ExceptionRaised += (_, ex) => Console.WriteLine($"[ssh] {ex.Message}");
        server.SessionAuthenticating += (_, e) =>
        {
            bool ok = e.AuthenticationType switch
            {
                SshAuthenticationType.ClientPassword => e.Username == "test" && e.Password == "test",
                _ => false,
            };
            Console.WriteLine($"[ssh] auth {e.AuthenticationType} {e.Username} -> {ok}");
            if (ok) e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, e.Username!) }, "test")))!;
        };
        server.SessionOpened += (_, session) =>
        {
            Console.WriteLine("[ssh] session opened");
            session.ChannelOpening += (_, e) =>
            {
                var channel = e.Channel;
                var shell = new FakeShell(new SshStream(channel), files, "test");
                channel.Request += (_, r) =>
                {
                    Console.WriteLine($"[ssh] channel request {r.RequestType}");
                    switch (r.RequestType)
                    {
                        case "pty-req":
                            var pty = r.Request.ConvertTo<TerminalRequestMessage>();
                            shell.Cols = (int)pty.Columns; shell.Rows = (int)pty.Rows;
                            r.IsAuthorized = true;
                            break;
                        case "window-change":
                        {
                            var raw = r.Request.ToBuffer().ToArray();
                            // byte type, uint32 channel, string "window-change", bool, uint32 cols, uint32 rows...
                            int off = 1 + 4;
                            int nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off)); off += 4 + nameLen + 1;
                            shell.Cols = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off));
                            shell.Rows = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off + 4));
                            Console.WriteLine($"[ssh] window-change {shell.Cols}x{shell.Rows}");
                            r.IsAuthorized = true;
                            break;
                        }
                        case "shell":
                            r.IsAuthorized = true;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                await shell.RunAsync(ct);
                                await channel.CloseAsync(ct);
                            });
                            break;
                        case "subsystem":
                        {
                            var raw = r.Request.ToBuffer().ToArray();
                            int off = 1 + 4;
                            int nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off)); off += 4 + nameLen + 1;
                            int subLen = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off));
                            var sub = Encoding.ASCII.GetString(raw, off + 4, subLen);
                            Console.WriteLine($"[ssh] subsystem {sub}");
                            r.IsAuthorized = sub == "sftp";
                            if (r.IsAuthorized)
                            {
                                var stream = new SshStream(channel);
                                _ = Task.Run(async () => { await new SftpServer(stream, files).RunAsync(ct); });
                            }
                            break;
                        }
                        case "env":
                            r.IsAuthorized = true;
                            break;
                    }
                };
            };
        };
        Console.WriteLine($"[ssh] listening on {port}");
        await server.AcceptSessionsAsync(port, IPAddress.Loopback);
    }
    catch (Exception ex) { Console.WriteLine($"[ssh] failed: {ex}"); }
}

// ---------------------------------------------------------------- Telnet

static async Task RunTelnet(int port, string files, CancellationToken ct)
{
    var l = new TcpListener(IPAddress.Loopback, port);
    l.Start();
    Console.WriteLine($"[telnet] listening on {port}");
    while (!ct.IsCancellationRequested)
    {
        var c = await l.AcceptTcpClientAsync(ct);
        _ = Task.Run(async () =>
        {
            using var conn = c;
            var ts = new TelnetStream(c.GetStream());
            ts.Negotiate();
            try
            {
                string? user = await ts.ReadLine("login: ", echo: true, ct);
                string? pass = await ts.ReadLine("Password: ", echo: false, ct);
                Console.WriteLine($"[telnet] login {user} ({ts.TermType}, {ts.Cols}x{ts.Rows})");
                if (user != "test" || pass != "test") { ts.WriteText("\r\nLogin incorrect\r\n"); return; }
                var shell = new FakeShell(ts, files, user) { Cols = ts.Cols, Rows = ts.Rows };
                ts.SizeChanged += (w, h) => { shell.Cols = w; shell.Rows = h; };
                await shell.RunAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            Console.WriteLine("[telnet] session ended");
        }, ct);
    }
}

// ---------------------------------------------------------------- Raw

static async Task RunRaw(int port, CancellationToken ct)
{
    var l = new TcpListener(IPAddress.Loopback, port);
    l.Start();
    Console.WriteLine($"[raw] listening on {port}");
    while (!ct.IsCancellationRequested)
    {
        var c = await l.AcceptTcpClientAsync(ct);
        _ = Task.Run(async () =>
        {
            using var conn = c;
            var s = c.GetStream();
            var reader = new StreamReader(s);
            var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\r\n" };
            await w.WriteLineAsync("raw echo server: every line you send comes back reversed");
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                    await w.WriteLineAsync("echo: " + new string(line.Reverse().ToArray()));
            }
            catch { }
        }, ct);
    }
}

/// <summary>Server side of Telnet: filters IAC, answers NAWS/TTYPE, escapes output.</summary>
sealed class TelnetStream : Stream
{
    private readonly NetworkStream _s;
    public int Cols = 80, Rows = 24;
    public string TermType = "";
    public event Action<int, int>? SizeChanged;
    private int _state;
    private readonly List<byte> _sb = new();

    public TelnetStream(NetworkStream s) { _s = s; }

    public void Negotiate()
    {
        // WILL ECHO, WILL SGA, DO NAWS, DO TTYPE
        _s.Write(new byte[] { 255, 251, 1, 255, 251, 3, 255, 253, 31, 255, 253, 24 });
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var raw = new byte[count];
        while (true)
        {
            int n = await _s.ReadAsync(raw.AsMemory(0, count), ct);
            if (n <= 0) return 0;
            int o = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = raw[i];
                switch (_state)
                {
                    case 0: if (b == 255) _state = 1; else if (b != 0) buffer[offset + o++] = b; break;
                    case 1:
                        if (b == 255) { buffer[offset + o++] = 255; _state = 0; }
                        else if (b == 250) { _state = 3; _sb.Clear(); }
                        else if (b >= 251 && b <= 254) _state = b == 251 ? 5 : 2;
                        else _state = 0;
                        break;
                    case 5: // WILL <opt>
                        if (b == 24) _s.Write(new byte[] { 255, 250, 24, 1, 255, 240 }); // TTYPE SEND
                        _state = 0;
                        break;
                    case 2: _state = 0; break;
                    case 3: if (b == 255) _state = 4; else _sb.Add(b); break;
                    case 4:
                        if (b == 240)
                        {
                            _state = 0;
                            if (_sb.Count >= 5 && _sb[0] == 31) { Cols = (_sb[1] << 8) | _sb[2]; Rows = (_sb[3] << 8) | _sb[4]; SizeChanged?.Invoke(Cols, Rows); Console.WriteLine($"[telnet] NAWS {Cols}x{Rows}"); }
                            if (_sb.Count >= 2 && _sb[0] == 24 && _sb[1] == 0) { TermType = Encoding.ASCII.GetString(_sb.ToArray(), 2, _sb.Count - 2); Console.WriteLine($"[telnet] TTYPE {TermType}"); }
                        }
                        else { _sb.Add(b); _state = 3; }
                        break;
                }
            }
            if (o > 0) return o;
        }
    }

    public async Task<string?> ReadLine(string prompt, bool echo, CancellationToken ct)
    {
        WriteText(prompt);
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await ReadAsync(one, 0, 1, ct);
            if (n <= 0) return null;
            char ch = (char)one[0];
            if (ch == '\r' || ch == '\n') { if (sb.Length == 0 && ch == '\n') continue; WriteText("\r\n"); return sb.ToString(); }
            if (ch == 127 || ch == 8) { if (sb.Length > 0) { sb.Length--; if (echo) WriteText("\b \b"); } continue; }
            if (ch < ' ') continue;
            sb.Append(ch);
            if (echo) WriteText(ch.ToString());
        }
    }

    public void WriteText(string s) { var b = Encoding.UTF8.GetBytes(s); Write(b, 0, b.Length); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var o = new List<byte>(count + 8);
        for (int i = offset; i < offset + count; i++) { o.Add(buffer[i]); if (buffer[i] == 255) o.Add(255); }
        _s.Write(o.ToArray());
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _s.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
