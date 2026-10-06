using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluenTTY.Models;

namespace FluenTTY.Remote;

/// <summary>
/// RFB (VNC) client, protocol versions 3.3, 3.7 and 3.8 (RFC 6143). Security: None and VNC
/// authentication. Encodings: Raw, CopyRect, RRE, Hextile, ZRLE, plus the DesktopSize and LastRect
/// pseudo-encodings. The framebuffer is kept as 32-bit BGRX pixels for a WriteableBitmap.
/// </summary>
public sealed class VncClient : IDisposable
{
    private Socket? _socket;
    private NetworkStream? _stream;
    private BufferedStream? _in;
    private readonly object _sendLock = new();
    private CancellationTokenSource? _cts;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";
    public string ServerVersion { get; private set; } = "";
    /// <summary>BGRX pixels, Width*Height. Lock <see cref="FrameLock"/> to read.</summary>
    public int[] Frame { get; private set; } = Array.Empty<int>();
    public object FrameLock { get; } = new();

    /// <summary>Raised on the reader thread after each framebuffer update with the changed rectangle (x, y, w, h).</summary>
    public event Action<int, int, int, int>? FrameUpdated;
    public event Action? DesktopResized;
    public event Action<string>? ClipboardReceived;
    public event Action? Bell;
    public event Action<string?>? Closed;

    private PixelFormat _pf;
    private int _closed;

    private struct PixelFormat
    {
        public byte Bpp, Depth;
        public bool BigEndian = false, TrueColour; // we always ask the server for little-endian pixels

        public PixelFormat() { }
        public ushort RMax, GMax, BMax;
        public byte RShift, GShift, BShift;
        public int BytesPerPixel => Bpp / 8;
    }

    // ================================================================== connect

    public async Task ConnectAsync(string host, int port, AppSettings settings, bool useProxy, Func<string?> askPassword,
        VncSettings vnc, bool viewOnly, Action<string> status, CancellationToken ct)
    {
        _socket = await Connections.SocketConnector.ConnectAsync(host, port, settings, useProxy, ct);
        _stream = new NetworkStream(_socket, ownsSocket: true);
        _in = new BufferedStream(_stream, 65536);

        // Version handshake
        var ver = Encoding.ASCII.GetString(ReadBytes(12)).TrimEnd('\n');
        ServerVersion = ver;
        status($"Server: {ver}");
        if (!ver.StartsWith("RFB ")) throw new IOException("Not a VNC server (no RFB greeting).");
        int major = int.Parse(ver.Substring(4, 3)), minor = int.Parse(ver.Substring(8, 3));
        int useMinor = major > 3 ? 8 : minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        Write(Encoding.ASCII.GetBytes($"RFB 003.00{useMinor}\n"));

        // Security
        int secType;
        if (useMinor >= 7)
        {
            int count = ReadU8();
            if (count == 0) throw new IOException("Server refused the connection: " + ReadString());
            var types = ReadBytes(count);
            // Prefer VNC authentication over "None" when both are offered: a server (or a man in the
            // middle) listing None first must not talk us out of authenticating.
            // Security review M7 — see CLAUDE.md "Security invariants".
            if (types.Contains((byte)2)) secType = 2;
            else if (types.Contains((byte)1)) secType = 1;
            else throw new IOException($"The server needs an authentication method FluenTTY doesn't support (types {string.Join(",", types)}). Allow standard VNC password authentication on the server.");
            Write(new[] { (byte)secType });
        }
        else
        {
            secType = (int)ReadU32();
            if (secType == 0) throw new IOException("Server refused the connection: " + ReadString());
        }

        if (secType == 1) status("The server allows connections without a password");
        if (secType == 2)
        {
            var challenge = ReadBytes(16);
            var password = askPassword() ?? throw new OperationCanceledException();
            Write(VncAuthResponse(challenge, password));
        }
        if (secType == 2 || useMinor >= 8)
        {
            uint result = ReadU32();
            if (result != 0)
            {
                string reason = useMinor >= 8 ? ReadString() : "";
                throw new UnauthorizedAccessException(string.IsNullOrEmpty(reason) ? "VNC authentication failed (wrong password?)" : reason);
            }
        }

        // Init
        Write(new[] { (byte)(vnc.Shared ? 1 : 0) });
        Width = ReadU16();
        Height = ReadU16();
        ReadBytes(16); // server's native pixel format; we set our own
        DesktopName = Encoding.UTF8.GetString(ReadBytes((int)ReadU32()));
        Frame = new int[Width * Height];
        status($"Desktop \"{DesktopName}\" {Width}×{Height}");

        _pf = vnc.ColorDepth switch
        {
            VncColorDepth.HighColor => new PixelFormat { Bpp = 16, Depth = 16, TrueColour = true, RMax = 31, GMax = 63, BMax = 31, RShift = 11, GShift = 5, BShift = 0 },
            VncColorDepth.Colors256 => new PixelFormat { Bpp = 8, Depth = 8, TrueColour = true, RMax = 7, GMax = 7, BMax = 3, RShift = 0, GShift = 3, BShift = 6 },
            _ => new PixelFormat { Bpp = 32, Depth = 24, TrueColour = true, RMax = 255, GMax = 255, BMax = 255, RShift = 16, GShift = 8, BShift = 0 },
        };
        SendPixelFormat();
        var encodings = vnc.Encoding switch
        {
            VncEncoding.Raw => new[] { 0 },
            VncEncoding.Hextile => new[] { 5, 1, 0 },
            VncEncoding.Zrle => new[] { 16, 1, 0 },
            _ => new[] { 16, 5, 1, 2, 0 },
        };
        SendEncodings(encodings.Concat(new[] { -223, -224 }).ToArray());
        RequestUpdate(false, 0, 0, Width, Height);

        _cts = new CancellationTokenSource();
        var thread = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true, Name = "VNC reader" };
        thread.Start();
    }

    /// <summary>VNC's DES challenge-response: the password (8 bytes, zero padded) is the key, with each byte's bits reversed.</summary>
    public static byte[] VncAuthResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < pw.Length; i++)
        {
            byte b = pw[i], r = 0;
            for (int bit = 0; bit < 8; bit++) if ((b & (1 << bit)) != 0) r |= (byte)(0x80 >> bit);
            key[i] = r;
        }
#pragma warning disable SYSLIB0021, CA5351 // DES is what the RFB protocol mandates
        using var des = DES.Create();
#pragma warning restore SYSLIB0021, CA5351
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    // ================================================================== client messages

    private void SendPixelFormat()
    {
        var m = new byte[20];
        m[0] = 0;
        m[4] = _pf.Bpp; m[5] = _pf.Depth; m[6] = (byte)(_pf.BigEndian ? 1 : 0); m[7] = (byte)(_pf.TrueColour ? 1 : 0);
        m[8] = (byte)(_pf.RMax >> 8); m[9] = (byte)_pf.RMax;
        m[10] = (byte)(_pf.GMax >> 8); m[11] = (byte)_pf.GMax;
        m[12] = (byte)(_pf.BMax >> 8); m[13] = (byte)_pf.BMax;
        m[14] = _pf.RShift; m[15] = _pf.GShift; m[16] = _pf.BShift;
        Write(m);
    }

    private void SendEncodings(int[] encodings)
    {
        var m = new byte[4 + encodings.Length * 4];
        m[0] = 2;
        m[2] = (byte)(encodings.Length >> 8); m[3] = (byte)encodings.Length;
        for (int i = 0; i < encodings.Length; i++)
        {
            int e = encodings[i];
            m[4 + i * 4] = (byte)(e >> 24); m[5 + i * 4] = (byte)(e >> 16); m[6 + i * 4] = (byte)(e >> 8); m[7 + i * 4] = (byte)e;
        }
        Write(m);
    }

    public void RequestUpdate(bool incremental, int x, int y, int w, int h)
    {
        Write(new byte[] { 3, (byte)(incremental ? 1 : 0), (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h });
    }

    public void SendKey(uint keysym, bool down)
    {
        Write(new byte[] { 4, (byte)(down ? 1 : 0), 0, 0, (byte)(keysym >> 24), (byte)(keysym >> 16), (byte)(keysym >> 8), (byte)keysym });
    }

    public void SendPointer(int mask, int x, int y)
    {
        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        Write(new byte[] { 5, (byte)mask, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y });
    }

    public void SendClipboard(string text)
    {
        var data = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var m = new byte[8 + data.Length];
        m[0] = 6;
        m[4] = (byte)(data.Length >> 24); m[5] = (byte)(data.Length >> 16); m[6] = (byte)(data.Length >> 8); m[7] = (byte)data.Length;
        data.CopyTo(m, 8);
        Write(m);
    }

    private void Write(byte[] data)
    {
        var s = _stream;
        if (s == null || _closed != 0) return;
        try
        {
            lock (_sendLock) s.Write(data, 0, data.Length);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Close($"Connection lost: {ex.Message}");
        }
    }

    // ================================================================== reading

    private byte[] ReadBytes(int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = _in!.Read(buf, got, n - got);
            if (r <= 0) throw new EndOfStreamException("The VNC server closed the connection.");
            got += r;
        }
        return buf;
    }

    private void ReadInto(Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = _in!.Read(buf[got..]);
            if (r <= 0) throw new EndOfStreamException("The VNC server closed the connection.");
            got += r;
        }
    }

    private int ReadU8()
    {
        int b = _in!.ReadByte();
        if (b < 0) throw new EndOfStreamException("The VNC server closed the connection.");
        return b;
    }

    private int ReadU16() { Span<byte> b = stackalloc byte[2]; ReadInto(b); return (b[0] << 8) | b[1]; }
    private uint ReadU32() { Span<byte> b = stackalloc byte[4]; ReadInto(b); return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]); }
    private int ReadS32() => (int)ReadU32();
    private string ReadString() => Encoding.UTF8.GetString(ReadBytes((int)Math.Min(ReadU32(), 65536)));

    private void ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int type = ReadU8();
                switch (type)
                {
                    case 0: ReadFramebufferUpdate(); RequestUpdate(true, 0, 0, Width, Height); break;
                    case 1:
                        ReadU8(); ReadU16();
                        int n = ReadU16();
                        ReadBytes(n * 6); // colour map entries (we always use true colour)
                        break;
                    case 2: Bell?.Invoke(); break;
                    case 3:
                        ReadBytes(3);
                        var len = ReadU32();
                        var text = Encoding.Latin1.GetString(ReadBytes((int)Math.Min(len, 10_000_000)));
                        ClipboardReceived?.Invoke(text.Replace("\n", "\r\n"));
                        break;
                    default:
                        throw new IOException($"Unknown VNC server message {type}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or EndOfStreamException or InvalidDataException)
        {
            Close(ex is EndOfStreamException ? null : ex.Message);
        }
        catch (Exception ex)
        {
            Close($"VNC protocol error: {ex.Message}");
        }
    }

    private void ReadFramebufferUpdate()
    {
        ReadU8();
        int rects = ReadU16();
        for (int i = 0; i < rects; i++)
        {
            int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
            int enc = ReadS32();
            switch (enc)
            {
                case 0: DecodeRaw(x, y, w, h); break;
                case 1: DecodeCopyRect(x, y, w, h); break;
                case 2: DecodeRre(x, y, w, h); break;
                case 5: DecodeHextile(x, y, w, h); break;
                case 16: DecodeZrle(x, y, w, h); break;
                case -223:
                    lock (FrameLock)
                    {
                        Width = w; Height = h;
                        Frame = new int[w * h];
                    }
                    DesktopResized?.Invoke();
                    continue;
                case -224: return; // LastRect
                default: throw new IOException($"Server sent unsupported encoding {enc}");
            }
            FrameUpdated?.Invoke(x, y, w, h);
        }
    }

    // ------------------------------------------------------------------ pixels

    private int ToBgr(uint v)
    {
        uint r = (v >> _pf.RShift) & _pf.RMax, g = (v >> _pf.GShift) & _pf.GMax, b = (v >> _pf.BShift) & _pf.BMax;
        if (_pf.RMax != 255) { r = r * 255 / _pf.RMax; g = g * 255 / _pf.GMax; b = b * 255 / _pf.BMax; }
        return (int)((r << 16) | (g << 8) | b);
    }

    private uint RawPixel(ReadOnlySpan<byte> p) => _pf.BytesPerPixel switch
    {
        1 => p[0],
        2 => (uint)(p[0] | (p[1] << 8)),
        _ => (uint)(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24)),
    };

    private int ReadPixel()
    {
        Span<byte> b = stackalloc byte[4];
        ReadInto(b[.._pf.BytesPerPixel]);
        return ToBgr(RawPixel(b));
    }

    private void Fill(int x, int y, int w, int h, int colour)
    {
        lock (FrameLock)
        {
            int fw = Width;
            for (int row = y; row < y + h && row < Height; row++)
                Array.Fill(Frame, colour, row * fw + x, Math.Min(w, fw - x));
        }
    }

    private void DecodeRaw(int x, int y, int w, int h)
    {
        int bpp = _pf.BytesPerPixel;
        var row = new byte[w * bpp];
        for (int r = 0; r < h; r++)
        {
            ReadInto(row);
            lock (FrameLock)
            {
                int off = (y + r) * Width + x;
                for (int c = 0; c < w; c++) Frame[off + c] = ToBgr(RawPixel(row.AsSpan(c * bpp)));
            }
        }
    }

    private void DecodeCopyRect(int x, int y, int w, int h)
    {
        int sx = ReadU16(), sy = ReadU16();
        lock (FrameLock)
        {
            var tmp = new int[w * h];
            for (int r = 0; r < h; r++) Array.Copy(Frame, (sy + r) * Width + sx, tmp, r * w, w);
            for (int r = 0; r < h; r++) Array.Copy(tmp, r * w, Frame, (y + r) * Width + x, w);
        }
    }

    private void DecodeRre(int x, int y, int w, int h)
    {
        uint n = ReadU32();
        Fill(x, y, w, h, ReadPixel());
        for (uint i = 0; i < n; i++)
        {
            int c = ReadPixel();
            int sx = ReadU16(), sy = ReadU16(), sw = ReadU16(), sh = ReadU16();
            Fill(x + sx, y + sy, sw, sh, c);
        }
    }

    private void DecodeHextile(int x, int y, int w, int h)
    {
        int bg = 0, fg = 0;
        for (int ty = y; ty < y + h; ty += 16)
        {
            for (int tx = x; tx < x + w; tx += 16)
            {
                int tw = Math.Min(16, x + w - tx), th = Math.Min(16, y + h - ty);
                int mask = ReadU8();
                if ((mask & 1) != 0) { DecodeRaw(tx, ty, tw, th); continue; }
                if ((mask & 2) != 0) bg = ReadPixel();
                Fill(tx, ty, tw, th, bg);
                if ((mask & 4) != 0) fg = ReadPixel();
                if ((mask & 8) == 0) continue;
                int count = ReadU8();
                bool coloured = (mask & 16) != 0;
                for (int i = 0; i < count; i++)
                {
                    int c = coloured ? ReadPixel() : fg;
                    int xy = ReadU8(), wh = ReadU8();
                    Fill(tx + (xy >> 4), ty + (xy & 15), (wh >> 4) + 1, (wh & 15) + 1, c);
                }
            }
        }
    }

    // ------------------------------------------------------------------ ZRLE

    private ChunkStream? _zChunks;
    private ZLibStream? _zlib;

    private sealed class ChunkStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _cur;
        private int _pos;
        public void Append(byte[] data) => _chunks.Enqueue(data);
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_cur == null || _pos >= _cur.Length)
            {
                if (_chunks.Count == 0) return 0;
                _cur = _chunks.Dequeue();
                _pos = 0;
            }
            int n = Math.Min(count, _cur.Length - _pos);
            Array.Copy(_cur, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private byte[] _zbuf = new byte[65536];
    private int _zlen, _zpos;

    private void DecodeZrle(int x, int y, int w, int h)
    {
        int len = (int)ReadU32();
        var compressed = ReadBytes(len);
        if (_zChunks == null)
        {
            _zChunks = new ChunkStream();
            _zlib = new ZLibStream(_zChunks, CompressionMode.Decompress);
        }
        _zChunks.Append(compressed);
        // Inflate everything this rectangle carries (the server flushes the zlib stream per rectangle).
        _zlen = 0; _zpos = 0;
        while (true)
        {
            if (_zlen == _zbuf.Length) Array.Resize(ref _zbuf, _zbuf.Length * 2);
            int n = _zlib!.Read(_zbuf, _zlen, _zbuf.Length - _zlen);
            if (n <= 0) break;
            _zlen += n;
        }

        int cpix = _pf.Bpp == 32 && _pf.Depth <= 24 ? 3 : _pf.BytesPerPixel;
        var palette = new int[128];
        for (int ty = y; ty < y + h; ty += 64)
        {
            for (int tx = x; tx < x + w; tx += 64)
            {
                int tw = Math.Min(64, x + w - tx), th = Math.Min(64, y + h - ty);
                int sub = ZByte();
                if (sub == 0)
                {
                    lock (FrameLock)
                        for (int r = 0; r < th; r++)
                            for (int c = 0; c < tw; c++) Frame[(ty + r) * Width + tx + c] = ZPixel(cpix);
                }
                else if (sub == 1)
                {
                    Fill(tx, ty, tw, th, ZPixel(cpix));
                }
                else if (sub <= 16)
                {
                    for (int i = 0; i < sub; i++) palette[i] = ZPixel(cpix);
                    int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4;
                    lock (FrameLock)
                    {
                        for (int r = 0; r < th; r++)
                        {
                            int shift = 8, cur = 0;
                            for (int c = 0; c < tw; c++)
                            {
                                if (shift == 8) { cur = ZByte(); shift = 0; }
                                shift += bits;
                                int idx = (cur >> (8 - shift)) & ((1 << bits) - 1);
                                Frame[(ty + r) * Width + tx + c] = palette[idx];
                            }
                        }
                    }
                }
                else if (sub == 128)
                {
                    int i = 0, total = tw * th;
                    while (i < total)
                    {
                        int colour = ZPixel(cpix);
                        int run = ZRunLength();
                        PlaceRun(tx, ty, tw, ref i, run, colour);
                    }
                }
                else if (sub >= 130)
                {
                    int size = sub - 128;
                    for (int k = 0; k < size; k++) palette[k] = ZPixel(cpix);
                    int i = 0, total = tw * th;
                    while (i < total)
                    {
                        int idx = ZByte();
                        int run = (idx & 128) != 0 ? ZRunLength() : 1;
                        PlaceRun(tx, ty, tw, ref i, run, palette[idx & 127]);
                    }
                }
                else throw new InvalidDataException($"Bad ZRLE subencoding {sub}");
            }
        }
    }

    private void PlaceRun(int tx, int ty, int tw, ref int i, int run, int colour)
    {
        lock (FrameLock)
        {
            for (int k = 0; k < run; k++, i++)
            {
                int r = i / tw, c = i % tw;
                int idx = (ty + r) * Width + tx + c;
                if (idx < Frame.Length) Frame[idx] = colour;
            }
        }
    }

    private int ZByte()
    {
        if (_zpos >= _zlen) throw new InvalidDataException("ZRLE data ended early");
        return _zbuf[_zpos++];
    }

    private int ZRunLength()
    {
        int len = 1, b;
        do { b = ZByte(); len += b; } while (b == 255);
        return len;
    }

    private int ZPixel(int bytes)
    {
        uint v = 0;
        for (int i = 0; i < bytes; i++) v |= (uint)ZByte() << (8 * i);
        return ToBgr(v);
    }

    // ================================================================== teardown

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _stream?.Dispose(); } catch { }
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Close(null);
        _zlib?.Dispose();
    }
}
