using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestServers;

/// <summary>Minimal plain-FTP server (passive mode, LIST in Unix format) for testing the FTP browser.</summary>
public sealed class FtpServer
{
    private readonly string _root;
    public FtpServer(string root) { _root = Path.GetFullPath(root); }

    public async Task ListenAsync(int port, CancellationToken ct)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        while (!ct.IsCancellationRequested)
        {
            var c = await l.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => Session(c, ct));
        }
    }

    private string Local(string cwd, string p)
    {
        var abs = p.StartsWith('/') ? p : cwd.TrimEnd('/') + "/" + p;
        var full = Path.GetFullPath(Path.Combine(_root, abs.TrimStart('/')));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : _root;
    }

    private async Task Session(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var s = client.GetStream();
        var reader = new StreamReader(s, Encoding.UTF8);
        var writer = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        string cwd = "/";
        TcpListener? pasv = null;
        string? renameFrom = null;
        bool authed = false;
        await writer.WriteLineAsync("220 FluenTTY test FTP");
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                var sp = line.IndexOf(' ');
                var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..];
                Console.WriteLine($"[ftp] {(cmd == "PASS" ? "PASS ****" : line)}");
                async Task<Stream?> Data()
                {
                    if (pasv == null) return null;
                    var dc = await pasv.AcceptTcpClientAsync(ct);
                    pasv.Stop(); pasv = null;
                    return dc.GetStream();
                }
                switch (cmd)
                {
                    case "USER": await writer.WriteLineAsync("331 Password required"); break;
                    case "PASS": authed = arg == "test"; await writer.WriteLineAsync(authed ? "230 Logged in" : "530 Login incorrect"); break;
                    case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8"); break;
                    case "FEAT": await writer.WriteLineAsync("211-Features:\r\n SIZE\r\n MDTM\r\n EPSV\r\n UTF8\r\n211 End"); break;
                    case "OPTS": await writer.WriteLineAsync("200 OK"); break;
                    case "PWD": await writer.WriteLineAsync($"257 \"{cwd}\""); break;
                    case "CWD":
                    {
                        var l = Local(cwd, arg);
                        if (Directory.Exists(l)) { var rel = Path.GetRelativePath(_root, l).Replace('\\', '/'); cwd = rel == "." ? "/" : "/" + rel; await writer.WriteLineAsync("250 OK"); }
                        else await writer.WriteLineAsync("550 No such directory");
                        break;
                    }
                    case "CDUP": cwd = "/"; await writer.WriteLineAsync("250 OK"); break;
                    case "TYPE": await writer.WriteLineAsync("200 Type set"); break;
                    case "PASV":
                    case "EPSV":
                        pasv = new TcpListener(IPAddress.Loopback, 0);
                        pasv.Start();
                        int p = ((IPEndPoint)pasv.LocalEndpoint).Port;
                        await writer.WriteLineAsync(cmd == "EPSV" ? $"229 Entering Extended Passive Mode (|||{p}|)" : $"227 Entering Passive Mode (127,0,0,1,{p >> 8},{p & 255})");
                        break;
                    case "LIST":
                    case "NLST":
                    {
                        var dir = Local(cwd, arg.StartsWith('-') ? "" : arg);
                        await writer.WriteLineAsync("150 Here comes the listing");
                        await using (var d = await Data())
                        {
                            if (d != null)
                            {
                                var sb = new StringBuilder();
                                foreach (var fi in new DirectoryInfo(dir).GetFileSystemInfos())
                                {
                                    bool isDir = fi is DirectoryInfo;
                                    long size = isDir ? 4096 : ((FileInfo)fi).Length;
                                    sb.Append(cmd == "NLST" ? fi.Name : $"{(isDir ? "drwxr-xr-x" : "-rw-r--r--")} 1 test test {size,10} {fi.LastWriteTime:MMM dd HH:mm} {fi.Name}").Append("\r\n");
                                }
                                var b = Encoding.UTF8.GetBytes(sb.ToString());
                                await d.WriteAsync(b, ct);
                            }
                        }
                        await writer.WriteLineAsync("226 Done");
                        break;
                    }
                    case "SIZE":
                    {
                        var f = Local(cwd, arg);
                        await writer.WriteLineAsync(File.Exists(f) ? $"213 {new FileInfo(f).Length}" : "550 No such file");
                        break;
                    }
                    case "MDTM":
                    {
                        var f = Local(cwd, arg);
                        await writer.WriteLineAsync(File.Exists(f) ? $"213 {File.GetLastWriteTimeUtc(f):yyyyMMddHHmmss}" : "550 No such file");
                        break;
                    }
                    case "RETR":
                    {
                        var f = Local(cwd, arg);
                        if (!File.Exists(f)) { await writer.WriteLineAsync("550 No such file"); break; }
                        await writer.WriteLineAsync("150 Sending");
                        await using (var d = await Data()) if (d != null) await d.WriteAsync(await File.ReadAllBytesAsync(f, ct), ct);
                        await writer.WriteLineAsync("226 Done");
                        break;
                    }
                    case "STOR":
                    {
                        var f = Local(cwd, arg);
                        await writer.WriteLineAsync("150 Receiving");
                        await using (var d = await Data())
                        await using (var fs = File.Create(f))
                            if (d != null) await d.CopyToAsync(fs, ct);
                        await writer.WriteLineAsync("226 Done");
                        break;
                    }
                    case "DELE": { var f = Local(cwd, arg); if (File.Exists(f)) { File.Delete(f); await writer.WriteLineAsync("250 Deleted"); } else await writer.WriteLineAsync("550 No such file"); break; }
                    case "MKD": Directory.CreateDirectory(Local(cwd, arg)); await writer.WriteLineAsync($"257 \"{arg}\" created"); break;
                    case "RMD": try { Directory.Delete(Local(cwd, arg), true); await writer.WriteLineAsync("250 Removed"); } catch { await writer.WriteLineAsync("550 Failed"); } break;
                    case "RNFR": renameFrom = Local(cwd, arg); await writer.WriteLineAsync("350 Ready"); break;
                    case "RNTO":
                        try { var to = Local(cwd, arg); if (Directory.Exists(renameFrom)) Directory.Move(renameFrom!, to); else File.Move(renameFrom!, to); await writer.WriteLineAsync("250 Renamed"); }
                        catch { await writer.WriteLineAsync("550 Failed"); }
                        break;
                    case "SITE": await writer.WriteLineAsync("200 OK"); break;
                    case "NOOP": await writer.WriteLineAsync("200 OK"); break;
                    case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                    default: await writer.WriteLineAsync("502 Not implemented"); break;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ftp] {ex.Message}"); }
    }
}
