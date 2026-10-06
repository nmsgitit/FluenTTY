using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluenTTY.Models;
using FluenTTY.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FluenTTY.Connections;

/// <summary>
/// Credentials gathered while connecting, kept in memory for the life of a tab so the SFTP browser and
/// reconnects don't ask again. Never written to disk unless the user saved a password in the session.
/// </summary>
public sealed class SshCredentials
{
    public string Username = "";
    public string? Password;
    public string? KeyFile;
    public string? Passphrase;
    /// <summary>Host keys the user accepted "once" during this tab's life (SHA256 fingerprints).</summary>
    public HashSet<string> AcceptedOnce = new();
}

/// <summary>
/// Connects SSH.NET clients (terminal and SFTP) the way PuTTY does: host key check against the
/// known-hosts store with a confirmation on first contact or change, then public key /
/// keyboard-interactive / password authentication with prompts shown inside the terminal.
/// </summary>
public static class SshConnector
{
    public static T Connect<T>(Func<ConnectionInfo, T> factory, SessionProfile profile, AppSettings settings,
        IConnectionUi ui, SshCredentials creds, CancellationToken ct) where T : BaseClient
    {
        var ssh = settings.Ssh;
        string host = profile.Host.Trim();
        int port = profile.EffectivePort;

        if (string.IsNullOrEmpty(creds.Username))
        {
            creds.Username = !string.IsNullOrEmpty(profile.Username) ? profile.Username
                : settings.Connection.UseWindowsUsername ? Environment.UserName
                : settings.Connection.AutoLoginUsername;
        }
        if (string.IsNullOrEmpty(creds.Username))
        {
            creds.Username = ui.Prompt("login as: ", echo: true) ?? throw new OperationCanceledException();
            if (string.IsNullOrWhiteSpace(creds.Username)) throw new OperationCanceledException();
        }
        creds.Password ??= SecretStore.Unprotect(profile.Password);
        if (creds.KeyFile == null)
        {
            var kf = !string.IsNullOrWhiteSpace(profile.PrivateKeyFile) ? profile.PrivateKeyFile : ssh.PrivateKeyFile;
            creds.KeyFile = string.IsNullOrWhiteSpace(kf) ? "" : Environment.ExpandEnvironmentVariables(kf.Trim('"'));
        }
        PrivateKeyFile? key = string.IsNullOrEmpty(creds.KeyFile) ? null : LoadKey(creds, ui);

        bool passwordPrompted = false;
        int attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var methods = new List<AuthenticationMethod>();
            if (key != null) methods.Add(new PrivateKeyAuthenticationMethod(creds.Username, key));
            if (creds.Password != null && ssh.TryPassword)
            {
                var pw = new PasswordAuthenticationMethod(creds.Username, creds.Password);
                pw.PasswordExpired += (_, e) =>
                {
                    ui.Display("\r\nYour password has expired.\r\n");
                    var np = ui.Prompt("New password: ", echo: false);
                    if (np != null) { e.NewPassword = System.Text.Encoding.UTF8.GetBytes(np); creds.Password = np; }
                };
                methods.Add(pw);
            }
            if (ssh.TryKeyboardInteractive)
            {
                var ki = new KeyboardInteractiveAuthenticationMethod(creds.Username);
                bool usedSaved = false;
                ki.AuthenticationPrompt += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Instruction)) ui.Display(e.Instruction.Replace("\n", "\r\n") + "\r\n");
                    foreach (var p in e.Prompts)
                    {
                        bool looksLikePassword = !p.IsEchoed && p.Request.Contains("assword", StringComparison.OrdinalIgnoreCase);
                        if (looksLikePassword && creds.Password != null && !usedSaved)
                        {
                            p.Response = creds.Password;
                            usedSaved = true;
                            continue;
                        }
                        var answer = ui.Prompt(p.Request, p.IsEchoed);
                        if (answer == null) throw new OperationCanceledException();
                        p.Response = answer;
                        if (looksLikePassword) creds.Password = answer;
                    }
                };
                methods.Add(ki);
            }
            if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(creds.Username));

            var info = BuildConnectionInfo(host, port, creds.Username, profile, settings, methods.ToArray());
            info.AuthenticationBanner += (_, e) =>
            {
                if (ssh.ShowBanner && !string.IsNullOrWhiteSpace(e.BannerMessage))
                    ui.Display(e.BannerMessage.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            };

            if (attempts == 0) ProbeReachable(host, port, profile, settings, ct);
            var client = factory(info);
            client.HostKeyReceived += (_, e) => VerifyHostKey(e, host, port, settings, ui, creds);
            try
            {
                ui.Status($"Connecting to {host} port {port}");
                client.ConnectAsync(ct).GetAwaiter().GetResult();
                ui.Status($"Server version: {info.ServerVersion}");
                ui.Status($"Key exchange: {info.CurrentKeyExchangeAlgorithm}, host key: {info.CurrentHostKeyAlgorithm}");
                ui.Status($"Cipher: {info.CurrentClientEncryption} / {info.CurrentServerEncryption}, MAC: {info.CurrentClientHmacAlgorithm}");
                ui.Status($"Authenticated as \"{creds.Username}\"");
                return client;
            }
            catch (SshAuthenticationException ex)
            {
                client.Dispose();
                var allowed = AllowedMethods(ex.Message);
                ui.Status($"Authentication failed: {ex.Message}");
                bool serverTakesPassword = allowed.Count == 0 || allowed.Contains("password") || allowed.Contains("keyboard-interactive");
                if (++attempts > Math.Max(1, ssh.MaxAuthAttempts) || !ssh.TryPassword || !serverTakesPassword)
                    throw new SshAuthenticationException(allowed.Count > 0 && !serverTakesPassword
                        ? $"Server refused our key and accepts only: {string.Join(", ", allowed)}"
                        : "Access denied");
                if (creds.Password != null || passwordPrompted) ui.Display("Access denied\r\n");
                creds.Password = ui.Prompt($"{creds.Username}@{host}'s password: ", echo: false);
                if (creds.Password == null) throw new OperationCanceledException();
                passwordPrompted = true;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>Fails fast (with the configured connect timeout) when the server can't be reached at all.</summary>
    private static void ProbeReachable(string host, int port, SessionProfile profile, AppSettings settings, CancellationToken ct)
    {
        if (profile.UseProxy && settings.Proxy.Type != ProxyKind.None && !SocketConnector.IsExcluded(host, settings.Proxy)) return;
        using var s = SocketConnector.ConnectAsync(host, port, settings, useProxy: false, ct).GetAwaiter().GetResult();
        try { s.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
    }

    private static List<string> AllowedMethods(string message)
    {
        var m = Regex.Match(message, @"\(([a-z\-,@.]+)\)");
        return m.Success ? m.Groups[1].Value.Split(',').ToList() : new List<string>();
    }

    private static PrivateKeyFile? LoadKey(SshCredentials creds, IConnectionUi ui)
    {
        var path = creds.KeyFile!;
        if (!File.Exists(path))
        {
            ui.Display($"Unable to use key file \"{path}\" (file not found)\r\n");
            return null;
        }
        for (int i = 0; i < 3; i++)
        {
            try
            {
                return creds.Passphrase == null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, creds.Passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                creds.Passphrase = ui.Prompt($"Passphrase for key \"{Path.GetFileName(path)}\": ", echo: false);
                if (creds.Passphrase == null) return null;
            }
            catch (Exception ex) when (creds.Passphrase != null && ex is SshException or CryptographicException or InvalidOperationException or ArgumentException)
            {
                ui.Display("Wrong passphrase\r\n");
                creds.Passphrase = ui.Prompt($"Passphrase for key \"{Path.GetFileName(path)}\": ", echo: false);
                if (creds.Passphrase == null) return null;
            }
            catch (Exception ex)
            {
                ui.Display($"Unable to load key file \"{Path.GetFileName(path)}\": {ex.Message}\r\n");
                return null;
            }
        }
        return null;
    }

    public static ConnectionInfo BuildConnectionInfo(string host, int port, string user, SessionProfile profile,
        AppSettings settings, AuthenticationMethod[] methods)
    {
        var proxy = settings.Proxy;
        ConnectionInfo info;
        bool viaProxy = profile.UseProxy && proxy.Type != ProxyKind.None && !SocketConnector.IsExcluded(host, proxy);
        if (viaProxy)
        {
            var type = proxy.Type switch { ProxyKind.Socks4 => ProxyTypes.Socks4, ProxyKind.Socks5 => ProxyTypes.Socks5, _ => ProxyTypes.Http };
            info = new ConnectionInfo(host, port, user, type, proxy.Host, proxy.Port,
                string.IsNullOrEmpty(proxy.Username) ? null : proxy.Username,
                SecretStore.Unprotect(proxy.Password), methods);
        }
        else
        {
            info = new ConnectionInfo(host, port, user, methods);
        }
        // SSH.NET applies this timeout to every wait during the handshake, including while our host key
        // and password prompts are open. PuTTY waits for the user indefinitely, so reachability is checked
        // separately (ProbeReachable, honouring Connection › timeout) and the handshake gets a long limit.
        info.Timeout = TimeSpan.FromMinutes(10);
        var ssh = settings.Ssh;
        ApplyOrder(info.KeyExchangeAlgorithms, ssh.KexAlgorithms);
        ApplyOrder(info.Encryptions, ssh.Ciphers);
        ApplyOrder(info.HmacAlgorithms, ssh.Macs);
        ApplyOrder(info.HostKeyAlgorithms, ssh.HostKeyAlgorithms);
        if (ssh.PreferKnownHostKeys)
        {
            // PuTTY's "Prefer algorithms for which a host key is known": avoids a spurious "new key" prompt.
            var known = SettingsStore.HostKeysFor(host, port).Select(k => k.Algorithm).ToHashSet();
            if (known.Count > 0) PromoteFirst(info.HostKeyAlgorithms, known);
        }

        bool compress = profile.Compression ?? ssh.Compression;
        var comp = info.CompressionAlgorithms.ToList();
        info.CompressionAlgorithms.Clear();
        foreach (var kv in comp.OrderBy(kv => compress ? (kv.Key == "none" ? 1 : 0) : (kv.Key == "none" ? 0 : 1)))
            if (compress || kv.Key == "none") info.CompressionAlgorithms[kv.Key] = kv.Value;
        if (info.CompressionAlgorithms.Count == 0) foreach (var kv in comp) info.CompressionAlgorithms[kv.Key] = kv.Value;
        return info;
    }

    /// <summary>Reorders/filters an SSH.NET algorithm table by the user's preference list (Settings › SSH).</summary>
    public static void ApplyOrder<TValue>(IDictionary<string, TValue> table, List<AlgorithmPreference> prefs)
    {
        if (prefs == null || prefs.Count == 0) return;
        var all = table.ToList();
        table.Clear();
        foreach (var p in prefs)
        {
            if (!p.Enabled) continue;
            var hit = all.FirstOrDefault(kv => kv.Key == p.Name);
            if (hit.Key != null) table[hit.Key] = hit.Value;
        }
        // Algorithms newer than the saved list are appended enabled (the list was saved by an older version).
        foreach (var kv in all)
            if (!prefs.Any(p => p.Name == kv.Key)) table[kv.Key] = kv.Value;
        if (table.Count == 0) foreach (var kv in all) table[kv.Key] = kv.Value;
    }

    private static void PromoteFirst<TValue>(IDictionary<string, TValue> table, HashSet<string> first)
    {
        var all = table.ToList();
        table.Clear();
        foreach (var kv in all.Where(kv => first.Contains(kv.Key))) table[kv.Key] = kv.Value;
        foreach (var kv in all.Where(kv => !first.Contains(kv.Key))) table[kv.Key] = kv.Value;
    }

    /// <summary>The algorithm names SSH.NET supports, in its default order (for the Settings lists).</summary>
    public static (List<string> kex, List<string> ciphers, List<string> macs, List<string> hostKeys) DefaultAlgorithms()
    {
        var info = new ConnectionInfo("localhost", "user", new NoneAuthenticationMethod("user"));
        return (info.KeyExchangeAlgorithms.Keys.ToList(), info.Encryptions.Keys.ToList(),
            info.HmacAlgorithms.Keys.ToList(), info.HostKeyAlgorithms.Keys.ToList());
    }

    private static void VerifyHostKey(HostKeyEventArgs e, string host, int port, AppSettings settings, IConnectionUi ui, SshCredentials creds)
    {
        string fp = "SHA256:" + e.FingerPrintSHA256;
        if (settings.Ssh.ManualHostKeys.Any(k => string.Equals(k.Trim(), fp, StringComparison.Ordinal)) || creds.AcceptedOnce.Contains(fp))
        {
            e.CanTrust = true;
            return;
        }

        var known = SettingsStore.FindHostKey(host, port, e.HostKeyName);
        if (known != null && known.FingerprintSha256 == fp)
        {
            e.CanTrust = true;
            return;
        }
        // A key of *another* type on record still means "this server is known": an attacker who can't
        // present the stored ed25519 key just offers RSA instead. Treat that as a changed key, like
        // PuTTY does, never as a first contact. Security review M2 — see CLAUDE.md "Security invariants".
        var otherType = known == null ? SettingsStore.HostKeysFor(host, port).FirstOrDefault() : null;

        var info = new HostKeyInfo
        {
            Host = host,
            Port = port,
            Algorithm = e.HostKeyName,
            FingerprintSha256 = fp,
            FingerprintMd5 = e.FingerPrintMD5,
            KeyBits = e.KeyLength,
            KeyBase64 = Convert.ToBase64String(e.HostKey),
            Mismatch = known != null || otherType != null,
            PreviousFingerprint = known?.FingerprintSha256 ?? otherType?.FingerprintSha256,
            PreviousAlgorithm = otherType?.Algorithm,
        };
        var answer = ui.ConfirmHostKey(info);
        if (answer == false)
        {
            e.CanTrust = false;
            return;
        }
        e.CanTrust = true;
        creds.AcceptedOnce.Add(fp);
        if (answer == true)
        {
            SettingsStore.StoreHostKey(new KnownHostKey
            {
                Host = host, Port = port, Algorithm = e.HostKeyName, FingerprintSha256 = fp, KeyBase64 = info.KeyBase64,
            });
            ui.Status($"Stored host key {e.HostKeyName} {fp}");
        }
    }
}
