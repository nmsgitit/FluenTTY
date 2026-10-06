using System.Net;
using System.Net.Sockets;
using System.Text;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Connections;

/// <summary>
/// Opens TCP connections for the protocols FluenTTY implements itself (Telnet, Rlogin, Rsh, Raw, VNC),
/// honouring Connection settings (address family, Nagle, keepalives, timeout) and Connection › Proxy
/// (SOCKS 4/4a, SOCKS 5, HTTP CONNECT — PuTTY's proxy.c does the same set minus the local command proxy).
/// </summary>
public static class SocketConnector
{
    public static async Task<Socket> ConnectAsync(string host, int port, AppSettings settings, bool useProxy,
        CancellationToken ct, int localPort = 0, IConnectionUi? ui = null)
    {
        var conn = settings.Connection;
        var proxy = settings.Proxy;
        bool viaProxy = useProxy && proxy.Type != ProxyKind.None && !IsExcluded(host, proxy);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, conn.ConnectTimeoutSeconds)));

        string targetHost = viaProxy ? proxy.Host : host;
        int targetPort = viaProxy ? proxy.Port : port;
        ui?.Status(viaProxy ? $"Connecting to proxy {targetHost}:{targetPort}" : $"Connecting to {host} port {port}");

        var addresses = await ResolveAsync(targetHost, conn.AddressFamily, timeout.Token);
        Exception? last = null;
        foreach (var addr in addresses)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = conn.TcpNoDelay;
                if (conn.TcpKeepalives) socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                if (localPort > 0) BindReservedPort(socket, addr.AddressFamily, localPort);
                await socket.ConnectAsync(new IPEndPoint(addr, targetPort), timeout.Token);
                if (viaProxy) await NegotiateProxyAsync(socket, host, port, proxy, timeout.Token);
                return socket;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = new TimeoutException($"Timed out connecting to {targetHost}:{targetPort}");
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
                if (ct.IsCancellationRequested) throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Rlogin/rsh servers insist on a client port below 1024 (Windows lets any user bind one).</summary>
    private static void BindReservedPort(Socket socket, AddressFamily family, int start)
    {
        var any = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        for (int p = start; p >= 512; p--)
        {
            try { socket.Bind(new IPEndPoint(any, p)); return; }
            catch (SocketException) { }
        }
    }

    public static async Task<IPAddress[]> ResolveAsync(string host, AddressFamilyChoice family, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return new[] { ip };
        var all = await Dns.GetHostAddressesAsync(host, ct);
        var filtered = family switch
        {
            AddressFamilyChoice.IPv4 => all.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
            AddressFamilyChoice.IPv6 => all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
            _ => all.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1),
        };
        var list = filtered.ToArray();
        if (list.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return list;
    }

    public static bool IsExcluded(string host, ProxySettings proxy)
    {
        bool local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                     || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        if (local && !proxy.ProxyLocalhost) return true;
        foreach (var raw in proxy.ExcludeList.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim();
            if (pattern.StartsWith("*.")) { if (host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true; }
            else if (pattern.StartsWith('.')) { if (host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true; }
            else if (pattern.Contains('/') && IPAddress.TryParse(host, out var hip) && IPNetwork.TryParse(pattern, out var net))
            {
                if (net.Contains(hip)) return true;
            }
            else if (pattern.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task NegotiateProxyAsync(Socket s, string host, int port, ProxySettings proxy, CancellationToken ct)
    {
        var stream = new NetworkStream(s, ownsSocket: false);
        bool remoteDns = proxy.DnsAtProxy != AutoOnOff.Off;
        switch (proxy.Type)
        {
            case ProxyKind.Http:
            {
                var sb = new StringBuilder();
                string target = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
                sb.Append($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
                if (!string.IsNullOrEmpty(proxy.Username))
                {
                    var pw = SecretStore.Unprotect(proxy.Password) ?? "";
                    sb.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{pw}"))).Append("\r\n");
                }
                sb.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
                var header = await ReadHttpHeaderAsync(stream, ct);
                var status = header.Split('\n')[0].Trim();
                var parts = status.Split(' ');
                if (parts.Length < 2 || parts[1] != "200") throw new IOException($"HTTP proxy refused the connection: {status}");
                break;
            }
            case ProxyKind.Socks4:
            {
                var req = new List<byte> { 4, 1, (byte)(port >> 8), (byte)port };
                byte[] user = Encoding.ASCII.GetBytes(proxy.Username ?? "");
                if (!remoteDns || IPAddress.TryParse(host, out _))
                {
                    var addr = (await ResolveAsync(host, AddressFamilyChoice.IPv4, ct))[0];
                    req.AddRange(addr.GetAddressBytes());
                    req.AddRange(user); req.Add(0);
                }
                else
                {
                    req.AddRange(new byte[] { 0, 0, 0, 1 }); // SOCKS 4a: let the proxy resolve
                    req.AddRange(user); req.Add(0);
                    req.AddRange(Encoding.ASCII.GetBytes(host)); req.Add(0);
                }
                await stream.WriteAsync(req.ToArray(), ct);
                var resp = await ReadExactAsync(stream, 8, ct);
                if (resp[1] != 90) throw new IOException($"SOCKS 4 proxy refused the connection (code {resp[1]})");
                break;
            }
            case ProxyKind.Socks5:
            {
                bool auth = !string.IsNullOrEmpty(proxy.Username);
                await stream.WriteAsync(auth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
                var sel = await ReadExactAsync(stream, 2, ct);
                if (sel[1] == 2)
                {
                    var u = Encoding.UTF8.GetBytes(proxy.Username!);
                    var p = Encoding.UTF8.GetBytes(SecretStore.Unprotect(proxy.Password) ?? "");
                    var authReq = new List<byte> { 1, (byte)u.Length };
                    authReq.AddRange(u); authReq.Add((byte)p.Length); authReq.AddRange(p);
                    await stream.WriteAsync(authReq.ToArray(), ct);
                    var ar = await ReadExactAsync(stream, 2, ct);
                    if (ar[1] != 0) throw new IOException("SOCKS 5 proxy rejected the username or password");
                }
                else if (sel[1] != 0) throw new IOException("SOCKS 5 proxy offered no acceptable authentication method");

                var req = new List<byte> { 5, 1, 0 };
                if (IPAddress.TryParse(host, out var ip))
                {
                    req.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
                    req.AddRange(ip.GetAddressBytes());
                }
                else if (remoteDns)
                {
                    var hb = Encoding.ASCII.GetBytes(host);
                    req.Add(3); req.Add((byte)hb.Length); req.AddRange(hb);
                }
                else
                {
                    var addr = (await ResolveAsync(host, AddressFamilyChoice.Auto, ct))[0];
                    req.Add(addr.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
                    req.AddRange(addr.GetAddressBytes());
                }
                req.Add((byte)(port >> 8)); req.Add((byte)port);
                await stream.WriteAsync(req.ToArray(), ct);
                var head = await ReadExactAsync(stream, 4, ct);
                if (head[1] != 0) throw new IOException($"SOCKS 5 proxy refused the connection (code {head[1]})");
                int skip = head[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactAsync(stream, 1, ct))[0], _ => 0 };
                await ReadExactAsync(stream, skip + 2, ct);
                break;
            }
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 16384)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) throw new IOException("Proxy closed the connection");
            buf.Add(one[0]);
            int c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) throw new IOException("Connection closed unexpectedly");
            got += n;
        }
        return buf;
    }
}
