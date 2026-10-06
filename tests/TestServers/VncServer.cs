using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TestServers;

/// <summary>
/// RFB 3.8 test server: VNC password "test", 800x600 test pattern with an animated square; clicks
/// paint dots; sends Raw or ZRLE (whichever the client prefers), honours SetPixelFormat.
/// </summary>
public sealed class VncServer
{
    private const int W = 800, H = 600;

    /// <param name="offerNone">Security test: also offer security type None (1). A client should still
    /// pick VNC authentication; the server accepts either and logs the choice.</param>
    public async Task ListenAsync(int port, CancellationToken ct, bool offerNone = false)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        while (!ct.IsCancellationRequested)
        {
            var c = await l.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => Session(c, offerNone, ct));
        }
    }

    private sealed class Client
    {
        public int Bpp = 32, Depth = 24, RShift = 16, GShift = 8, BShift = 0, RMax = 255, GMax = 255, BMax = 255;
        public bool Zrle;
        public int[] Fb = new int[W * H];
        public bool Pending;
        public (int x, int y, int w, int h)? Dirty = (0, 0, W, H);
        public MemoryStream ZOut = new();
        public ZLibStream? Z;
        public long ZPos;
    }

    private static void Pattern(int[] fb)
    {
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int r = x * 255 / W, g = y * 255 / H, b = 160;
                if ((x / 40 + y / 40) % 2 == 0 && y > 420) { r = g = b = 230; }
                fb[y * W + x] = (r << 16) | (g << 8) | b;
            }
        void Rect(int x, int y, int w, int h, int c) { for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) fb[yy * W + xx] = c; }
        Rect(40, 40, 200, 120, 0xE81123); Rect(260, 40, 200, 120, 0x16C60C); Rect(480, 40, 200, 120, 0x0078D4);
        Rect(40, 200, 640, 20, 0xFFFFFF); Rect(40, 240, 640, 20, 0x000000);
    }

    private static byte[] DesResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < pw.Length; i++)
        {
            byte bb = pw[i], rr = 0;
            for (int bit = 0; bit < 8; bit++) if ((bb & (1 << bit)) != 0) rr |= (byte)(0x80 >> bit);
            key[i] = rr;
        }
#pragma warning disable SYSLIB0021, CA5351
        using var des = DES.Create();
#pragma warning restore SYSLIB0021, CA5351
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    private static async Task ReadExact(Stream s, byte[] b, CancellationToken ct)
    {
        int got = 0;
        while (got < b.Length) { int n = await s.ReadAsync(b.AsMemory(got), ct); if (n <= 0) throw new EndOfStreamException(); got += n; }
    }

    private async Task Session(TcpClient tcp, bool offerNone, CancellationToken ct)
    {
        using var conn = tcp;
        var s = tcp.GetStream();
        var sendLock = new SemaphoreSlim(1, 1);
        var c = new Client();
        Pattern(c.Fb);
        try
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);
            var ver = new byte[12]; await ReadExact(s, ver, ct);
            Console.WriteLine($"[vnc] client {Encoding.ASCII.GetString(ver).Trim()}");
            await s.WriteAsync(offerNone ? new byte[] { 2, 1, 2 } : new byte[] { 1, 2 }, ct);
            var chosen = new byte[1]; await ReadExact(s, chosen, ct);
            Console.WriteLine($"[vnc] client chose security type {chosen[0]}{(chosen[0] == 1 ? " (None)" : " (VNC auth)")}");
            bool ok = true;
            if (chosen[0] != 1)
            {
                var challenge = RandomNumberGenerator.GetBytes(16);
                await s.WriteAsync(challenge, ct);
                var resp = new byte[16]; await ReadExact(s, resp, ct);
                ok = resp.AsSpan().SequenceEqual(DesResponse(challenge, "test"));
            }
            if (!ok)
            {
                var reason = Encoding.ASCII.GetBytes("Authentication failed");
                var fail = new byte[8 + reason.Length];
                fail[3] = 1; BinaryPrimitives.WriteUInt32BigEndian(fail.AsSpan(4), (uint)reason.Length); reason.CopyTo(fail, 8);
                await s.WriteAsync(fail, ct);
                Console.WriteLine("[vnc] bad password");
                return;
            }
            await s.WriteAsync(new byte[4], ct);
            var shared = new byte[1]; await ReadExact(s, shared, ct);
            var name = Encoding.UTF8.GetBytes("FluenTTY test desktop");
            var init = new byte[24 + name.Length];
            BinaryPrimitives.WriteUInt16BigEndian(init, W); BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), H);
            init[4] = 32; init[5] = 24; init[6] = 0; init[7] = 1;
            init[8] = 0; init[9] = 255; init[10] = 0; init[11] = 255; init[12] = 0; init[13] = 255; init[14] = 16; init[15] = 8; init[16] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length); name.CopyTo(init, 24);
            await s.WriteAsync(init, ct);
            Console.WriteLine("[vnc] session started");

            // Animation: a square that moves every 400 ms.
            _ = Task.Run(async () =>
            {
                int pos = 0;
                while (!ct.IsCancellationRequested && tcp.Connected)
                {
                    await Task.Delay(400, ct);
                    lock (c)
                    {
                        int x0 = 40 + pos * 20 % 600, y0 = 300;
                        for (int y = y0; y < y0 + 60; y++) for (int x = 40; x < 700; x++) c.Fb[y * W + x] = 0x202020;
                        for (int y = y0 + 10; y < y0 + 50; y++) for (int x = x0; x < x0 + 40; x++) c.Fb[y * W + x] = 0xFFB900;
                        Union(c, 40, y0, 660, 60);
                        pos++;
                    }
                    await Flush(s, c, sendLock, ct);
                }
            }, ct);

            var hdr = new byte[1];
            while (true)
            {
                await ReadExact(s, hdr, ct);
                switch (hdr[0])
                {
                    case 0:
                    {
                        var b = new byte[19]; await ReadExact(s, b, ct);
                        lock (c)
                        {
                            c.Bpp = b[3]; c.Depth = b[4]; c.RMax = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(7)); c.GMax = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(9));
                            c.BMax = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(11)); c.RShift = b[13]; c.GShift = b[14]; c.BShift = b[15];
                        }
                        Console.WriteLine($"[vnc] pixel format {c.Bpp} bpp");
                        break;
                    }
                    case 2:
                    {
                        var b = new byte[3]; await ReadExact(s, b, ct);
                        int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1));
                        var e = new byte[n * 4]; await ReadExact(s, e, ct);
                        var list = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadInt32BigEndian(e.AsSpan(i * 4))).ToList();
                        c.Zrle = list.Contains(16) && list.IndexOf(16) < Math.Max(0, list.IndexOf(0) < 0 ? int.MaxValue : list.IndexOf(0));
                        Console.WriteLine($"[vnc] encodings {string.Join(",", list)} -> {(c.Zrle ? "ZRLE" : "Raw")}");
                        break;
                    }
                    case 3:
                    {
                        var b = new byte[9]; await ReadExact(s, b, ct);
                        lock (c) { c.Pending = true; if (b[0] == 0) Union(c, 0, 0, W, H); }
                        await Flush(s, c, sendLock, ct);
                        break;
                    }
                    case 4:
                    {
                        var b = new byte[7]; await ReadExact(s, b, ct);
                        uint sym = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(3));
                        Console.WriteLine($"[vnc] key {(b[0] != 0 ? "down" : "up")} 0x{sym:X}{(sym is >= 0x20 and < 0x7F ? $" '{(char)sym}'" : "")}");
                        break;
                    }
                    case 5:
                    {
                        var b = new byte[5]; await ReadExact(s, b, ct);
                        int mask = b[0], x = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1)), y = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(3));
                        if (mask != 0)
                        {
                            Console.WriteLine($"[vnc] pointer {mask} at {x},{y}");
                            lock (c)
                            {
                                for (int yy = Math.Max(0, y - 3); yy < Math.Min(H, y + 4); yy++) for (int xx = Math.Max(0, x - 3); xx < Math.Min(W, x + 4); xx++) c.Fb[yy * W + xx] = 0xFFFFFF;
                                Union(c, Math.Max(0, x - 3), Math.Max(0, y - 3), 7, 7);
                            }
                            await Flush(s, c, sendLock, ct);
                        }
                        break;
                    }
                    case 6:
                    {
                        var b = new byte[7]; await ReadExact(s, b, ct);
                        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(3));
                        var t = new byte[len]; await ReadExact(s, t, ct);
                        var text = Encoding.Latin1.GetString(t);
                        Console.WriteLine($"[vnc] clipboard from client: {text}");
                        var reply = Encoding.Latin1.GetBytes("server got: " + text);
                        var m = new byte[8 + reply.Length]; m[0] = 3; BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)reply.Length); reply.CopyTo(m, 8);
                        await sendLock.WaitAsync(ct); try { await s.WriteAsync(m, ct); } finally { sendLock.Release(); }
                        break;
                    }
                    default:
                        Console.WriteLine($"[vnc] unknown message {hdr[0]}");
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or SocketException)
        {
            Console.WriteLine("[vnc] session ended");
        }
    }

    private static void Union(Client c, int x, int y, int w, int h)
    {
        if (c.Dirty is not { } d) { c.Dirty = (x, y, w, h); return; }
        int x1 = Math.Min(d.x, x), y1 = Math.Min(d.y, y), x2 = Math.Max(d.x + d.w, x + w), y2 = Math.Max(d.y + d.h, y + h);
        c.Dirty = (x1, y1, x2 - x1, y2 - y1);
    }

    private static uint Pixel(Client c, int rgb)
    {
        uint r = (uint)((rgb >> 16) & 255) * (uint)c.RMax / 255, g = (uint)((rgb >> 8) & 255) * (uint)c.GMax / 255, b = (uint)(rgb & 255) * (uint)c.BMax / 255;
        return (r << c.RShift) | (g << c.GShift) | (b << c.BShift);
    }

    private static void PutPixel(Stream o, Client c, int rgb, bool cpixel)
    {
        uint v = Pixel(c, rgb);
        int bytes = cpixel && c.Bpp == 32 && c.Depth <= 24 ? 3 : c.Bpp / 8;
        for (int i = 0; i < bytes; i++) o.WriteByte((byte)(v >> (8 * i)));
    }

    private static async Task Flush(Stream s, Client c, SemaphoreSlim sendLock, CancellationToken ct)
    {
        byte[] msg;
        lock (c)
        {
            if (!c.Pending || c.Dirty is not { } d) return;
            c.Pending = false;
            c.Dirty = null;
            var m = new MemoryStream();
            m.WriteByte(0); m.WriteByte(0); m.WriteByte(0); m.WriteByte(1);
            Span<byte> hdr = stackalloc byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(hdr, (ushort)d.x); BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], (ushort)d.y);
            BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)d.w); BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], (ushort)d.h);
            BinaryPrimitives.WriteInt32BigEndian(hdr[8..], c.Zrle ? 16 : 0);
            m.Write(hdr);
            if (!c.Zrle)
            {
                for (int y = d.y; y < d.y + d.h; y++) for (int x = d.x; x < d.x + d.w; x++) PutPixel(m, c, c.Fb[y * W + x], false);
            }
            else
            {
                c.Z ??= new ZLibStream(c.ZOut, CompressionLevel.Fastest, leaveOpen: true);
                var raw = new MemoryStream();
                for (int ty = d.y; ty < d.y + d.h; ty += 64)
                    for (int tx = d.x; tx < d.x + d.w; tx += 64)
                    {
                        int tw = Math.Min(64, d.x + d.w - tx), th = Math.Min(64, d.y + d.h - ty);
                        // Alternate raw (0) and palette-less RLE (128) tiles to exercise both decoders.
                        if (((tx / 64) + (ty / 64)) % 2 == 0)
                        {
                            raw.WriteByte(0);
                            for (int y = ty; y < ty + th; y++) for (int x = tx; x < tx + tw; x++) PutPixel(raw, c, c.Fb[y * W + x], true);
                        }
                        else
                        {
                            raw.WriteByte(128);
                            int i = 0, total = tw * th;
                            while (i < total)
                            {
                                int colour = c.Fb[(ty + i / tw) * W + tx + i % tw];
                                int run = 1;
                                while (i + run < total && c.Fb[(ty + (i + run) / tw) * W + tx + (i + run) % tw] == colour) run++;
                                PutPixel(raw, c, colour, true);
                                int left = run - 1;
                                while (left >= 255) { raw.WriteByte(255); left -= 255; }
                                raw.WriteByte((byte)left);
                                i += run;
                            }
                        }
                    }
                c.Z.Write(raw.ToArray());
                c.Z.Flush();
                var all = c.ZOut.ToArray();
                var chunk = all[(int)c.ZPos..];
                c.ZPos = all.Length;
                Span<byte> len = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(len, (uint)chunk.Length);
                m.Write(len);
                m.Write(chunk);
            }
            msg = m.ToArray();
        }
        await sendLock.WaitAsync(ct);
        try { await s.WriteAsync(msg, ct); } finally { sendLock.Release(); }
    }
}
