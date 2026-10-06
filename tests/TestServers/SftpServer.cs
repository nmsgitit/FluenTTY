using System.Buffers.Binary;
using System.Text;

namespace TestServers;

/// <summary>
/// Minimal SFTP v3 server (draft-ietf-secsh-filexfer-02) over a stream, rooted at a sandbox folder.
/// Also serves a virtual, hostile <c>/evil</c> tree for security tests: names with path traversal, an
/// absolute Windows path, a right-to-left override and an endlessly nested <c>loop</c> folder. Every
/// escape target lies inside the sandbox folder's parent, so a vulnerable client only writes there.
/// </summary>
public sealed class SftpServer
{
    private readonly Stream _io;
    private readonly string _root;
    private readonly Dictionary<string, object> _handles = new();
    private int _next;

    public SftpServer(Stream io, string root) { _io = io; _root = Path.GetFullPath(root); }

    // ------------------------------------------------------------------ hostile virtual tree

    private sealed record VEntry(string Name, bool Dir);

    /// <summary>Children of a virtual directory, or null when <paramref name="path"/> isn't one.</summary>
    private List<VEntry>? VDir(string path)
    {
        path = path.TrimEnd('/');
        if (path == "/evil")
            return new()
            {
                new(@"..\..\escaped-backslash.txt", false),
                new("../escaped-slash.txt", false),
                new(Path.Combine(Path.GetDirectoryName(_root)!, "rooted-escape.txt"), false),
                new("invoice‮txt.bat", false),
                new("sub", true),
                new("loop", true),
            };
        if (path == "/evil/sub") return new() { new(@"..\..\..\deep-escape.txt", false) };
        if (System.Text.RegularExpressions.Regex.IsMatch(path, "^/evil/loop(/loop)*$")) return new() { new("loop", true), new("ok.txt", false) };
        return null;
    }

    /// <summary>The virtual entry at <paramref name="path"/> (names may contain '/' and '\').</summary>
    private VEntry? VLookup(string path)
    {
        if (VDir(path) != null) return new(path, true);
        foreach (var dir in new[] { "/evil", "/evil/sub" }.Concat(LoopPrefixes(path)))
        {
            if (!path.StartsWith(dir + "/")) continue;
            var name = path[(dir.Length + 1)..];
            var hit = VDir(dir)?.FirstOrDefault(e => e.Name == name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static IEnumerable<string> LoopPrefixes(string path)
    {
        var p = "/evil/loop";
        while (path.StartsWith(p + "/")) { yield return p; p += "/loop"; }
    }

    private static W VAttrs(W w, bool dir, long size) =>
        w.U32(1 | 2 | 4 | 8).U64((ulong)size).U32(1000).U32(1000).U32(dir ? 0x4000u | 0x1EDu : 0x8000u | 0x1A4u).U32(0).U32(0);

    private static byte[] VContent(string path) => Encoding.UTF8.GetBytes("HOSTILE " + path + "\n");

    private string Local(string p)
    {
        if (string.IsNullOrEmpty(p) || p == ".") p = "/";
        var full = Path.GetFullPath(Path.Combine(_root, p.Replace('\\', '/').TrimStart('/')));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : _root;
    }

    private string Remote(string local)
    {
        var rel = Path.GetRelativePath(_root, local).Replace('\\', '/');
        return rel == "." ? "/" : "/" + rel;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExact(lenBuf, ct)) return;
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            var pkt = new byte[len];
            if (!await ReadExact(pkt, ct)) return;
            try { Handle(pkt); }
            catch (Exception ex) { Console.WriteLine($"[sftp] {ex.Message}"); }
        }
    }

    private async Task<bool> ReadExact(byte[] b, CancellationToken ct)
    {
        int got = 0;
        while (got < b.Length)
        {
            int n = await _io.ReadAsync(b.AsMemory(got), ct);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private sealed class R
    {
        private readonly byte[] _b; private int _p;
        public R(byte[] b, int p) { _b = b; _p = p; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(_b.AsSpan(_p)); _p += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64BigEndian(_b.AsSpan(_p)); _p += 8; return v; }
        public byte[] Bytes() { int n = (int)U32(); var v = _b.AsSpan(_p, n).ToArray(); _p += n; return v; }
        public string Str() => Encoding.UTF8.GetString(Bytes());
        public void Attrs()
        {
            uint f = U32();
            if ((f & 1) != 0) U64();
            if ((f & 2) != 0) { U32(); U32(); }
            if ((f & 4) != 0) U32();
            if ((f & 8) != 0) { U32(); U32(); }
        }
    }

    private sealed class W
    {
        private readonly MemoryStream _m = new();
        public W B(byte v) { _m.WriteByte(v); return this; }
        public W U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(s, v); _m.Write(s); return this; }
        public W U64(ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(s, v); _m.Write(s); return this; }
        public W Bytes(byte[] b) { U32((uint)b.Length); _m.Write(b); return this; }
        public W Str(string s) => Bytes(Encoding.UTF8.GetBytes(s));
        public byte[] ToArray() => _m.ToArray();
    }

    private void Send(W w)
    {
        var body = w.ToArray();
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        lock (_io) { _io.Write(len); _io.Write(body); _io.Flush(); }
    }

    private void Status(uint id, uint code, string msg = "") => Send(new W().B(101).U32(id).U32(code).Str(msg).Str(""));

    private static W Attrs(W w, FileSystemInfo fi)
    {
        bool dir = fi is DirectoryInfo;
        uint perm = dir ? 0x4000u | 0x1EDu : 0x8000u | 0x1A4u;
        uint mtime = (uint)new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        return w.U32(1 | 2 | 4 | 8).U64(dir ? 4096UL : (ulong)((FileInfo)fi).Length).U32(1000).U32(1000).U32(perm).U32(mtime).U32(mtime);
    }

    private static string LongName(FileSystemInfo fi)
    {
        bool dir = fi is DirectoryInfo;
        long size = dir ? 4096 : ((FileInfo)fi).Length;
        return $"{(dir ? "drwxr-xr-x" : "-rw-r--r--")} 1 test test {size,8} {fi.LastWriteTime:MMM dd HH:mm} {fi.Name}";
    }

    private void Handle(byte[] p)
    {
        byte type = p[0];
        var r = new R(p, 1);
        if (type == 1) { Send(new W().B(2).U32(3)); return; }
        uint id = r.U32();
        switch (type)
        {
            case 16: // REALPATH
            {
                var path = r.Str();
                if (VLookup(path) != null) { Send(new W().B(104).U32(id).U32(1).Str(path).Str(path).U32(0)); break; }
                var local = Local(path.StartsWith('/') ? path : "/" + path);
                var name = Remote(local);
                Send(new W().B(104).U32(id).U32(1).Str(name).Str(name).U32(0));
                break;
            }
            case 17: case 7: // STAT, LSTAT
            {
                var path = r.Str();
                if (VLookup(path) is { } v) { Send(VAttrs(new W().B(105).U32(id), v.Dir, v.Dir ? 4096 : VContent(path).Length)); break; }
                var local = Local(path);
                FileSystemInfo? fi = Directory.Exists(local) ? new DirectoryInfo(local) : File.Exists(local) ? new FileInfo(local) : null;
                if (fi == null) { Status(id, 2, "No such file"); break; }
                Send(Attrs(new W().B(105).U32(id), fi));
                break;
            }
            case 8: // FSTAT
            {
                var h = Encoding.UTF8.GetString(r.Bytes());
                if (_handles.GetValueOrDefault(h) is FileStream fs) Send(Attrs(new W().B(105).U32(id), new FileInfo(fs.Name)));
                else if (_handles.GetValueOrDefault(h) is MemoryStream ms) Send(VAttrs(new W().B(105).U32(id), false, ms.Length));
                else Status(id, 4);
                break;
            }
            case 11: // OPENDIR
            {
                var path = r.Str();
                if (VDir(path) is { } vdir)
                {
                    var vh = $"d{_next++}";
                    _handles[vh] = new Queue<object>(vdir.Select(e => (object)(path.TrimEnd('/'), e)));
                    Send(new W().B(102).U32(id).Str(vh));
                    break;
                }
                var local = Local(path);
                if (!Directory.Exists(local)) { Status(id, 2, "No such directory"); break; }
                var entries = new DirectoryInfo(local).GetFileSystemInfos().Cast<object>().ToList();
                if (path.TrimEnd('/') is "" or "/") entries.Add(("", new VEntry("evil", true)));
                var h = $"d{_next++}";
                _handles[h] = new Queue<object>(entries);
                Send(new W().B(102).U32(id).Str(h));
                break;
            }
            case 12: // READDIR
            {
                var h = Encoding.UTF8.GetString(r.Bytes());
                if (_handles.GetValueOrDefault(h) is not Queue<object> q) { Status(id, 4); break; }
                if (q.Count == 0) { Status(id, 1, "EOF"); break; }
                var batch = new List<object>();
                while (q.Count > 0 && batch.Count < 50) batch.Add(q.Dequeue());
                var w = new W().B(104).U32(id).U32((uint)batch.Count);
                foreach (var o in batch)
                {
                    if (o is FileSystemInfo fi) Attrs(w.Str(fi.Name).Str(LongName(fi)), fi);
                    else if (o is ValueTuple<string, VEntry> ve)
                    {
                        var (dir, e) = ve;
                        long size = e.Dir ? 4096 : VContent(dir + "/" + e.Name).Length;
                        VAttrs(w.Str(e.Name).Str($"{(e.Dir ? "d" : "-")}rw-r--r-- 1 evil evil {size,8} Jan 01 00:00 {e.Name}"), e.Dir, size);
                    }
                }
                Send(w);
                break;
            }
            case 3: // OPEN
            {
                var path = r.Str();
                if (VLookup(path) is { Dir: false })
                {
                    r.U32(); r.Attrs();
                    var vh = $"f{_next++}";
                    _handles[vh] = new MemoryStream(VContent(path), writable: false);
                    Console.WriteLine($"[sftp] hostile file served: {path}");
                    Send(new W().B(102).U32(id).Str(vh));
                    break;
                }
                var local = Local(path);
                uint flags = r.U32();
                r.Attrs();
                try
                {
                    FileMode mode = (flags & 0x10) != 0 ? FileMode.Create : (flags & 8) != 0 ? FileMode.OpenOrCreate : FileMode.Open;
                    FileAccess access = (flags & 2) != 0 ? ((flags & 1) != 0 ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read;
                    var fs = new FileStream(local, mode, access, FileShare.ReadWrite);
                    if ((flags & 4) != 0) fs.Seek(0, SeekOrigin.End);
                    var h = $"f{_next++}";
                    _handles[h] = fs;
                    Send(new W().B(102).U32(id).Str(h));
                }
                catch (FileNotFoundException) { Status(id, 2, "No such file"); }
                catch (Exception ex) { Status(id, 4, ex.Message); }
                break;
            }
            case 5: // READ
            {
                var h = Encoding.UTF8.GetString(r.Bytes());
                ulong off = r.U64(); uint len = r.U32();
                if (_handles.GetValueOrDefault(h) is not Stream fs) { Status(id, 4); break; }
                if ((long)off >= fs.Length) { Status(id, 1, "EOF"); break; }
                fs.Position = (long)off;
                var buf = new byte[Math.Min(len, 65536)];
                int n = fs.Read(buf, 0, buf.Length);
                Send(new W().B(103).U32(id).Bytes(buf[..n]));
                break;
            }
            case 6: // WRITE
            {
                var h = Encoding.UTF8.GetString(r.Bytes());
                ulong off = r.U64();
                var data = r.Bytes();
                if (_handles.GetValueOrDefault(h) is not FileStream fs) { Status(id, 4); break; }
                fs.Position = (long)off;
                fs.Write(data);
                Status(id, 0);
                break;
            }
            case 4: // CLOSE
            {
                var h = Encoding.UTF8.GetString(r.Bytes());
                if (_handles.Remove(h, out var o) && o is IDisposable d) d.Dispose();
                Status(id, 0);
                break;
            }
            case 13: { var l = Local(r.Str()); if (File.Exists(l)) { File.Delete(l); Status(id, 0); } else Status(id, 2); break; }
            case 14: { var l = Local(r.Str()); Directory.CreateDirectory(l); Status(id, 0); break; }
            case 15: { var l = Local(r.Str()); try { Directory.Delete(l); Status(id, 0); } catch (Exception ex) { Status(id, 4, ex.Message); } break; }
            case 18:
            {
                var from = Local(r.Str()); var to = Local(r.Str());
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                    Status(id, 0);
                }
                catch (Exception ex) { Status(id, 4, ex.Message); }
                break;
            }
            case 9: case 10: Status(id, 0); break; // SETSTAT / FSETSTAT: accepted (Windows can't chmod)
            default: Status(id, 8, "Unsupported"); break;
        }
    }
}
