using System.Net.Security;
using FluentFTP;
using FluenTTY.Connections;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Files;

/// <summary>FTP / FTPS (explicit or implicit TLS) through FluentFTP.</summary>
public sealed class FtpFileSystem : IRemoteFileSystem
{
    private readonly SessionProfile _profile;
    private readonly AppSettings _settings;
    private readonly IConnectionUi _ui;
    private AsyncFtpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _user = "";

    public FtpFileSystem(SessionProfile profile, AppSettings settings, IConnectionUi ui)
    {
        _profile = profile;
        _settings = settings;
        _ui = ui;
    }

    public string DisplayName => $"ftp://{(_user.Length > 0 ? _user + "@" : "")}{_profile.HostWithPort}";
    public bool IsConnected => _client?.IsConnected == true;
    public bool IsEncrypted => _client?.IsEncrypted == true;
    public bool SupportsPermissions => true;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _user = !string.IsNullOrEmpty(_profile.Username) ? _profile.Username : _settings.Connection.AutoLoginUsername;
        if (string.IsNullOrEmpty(_user)) _user = await Task.Run(() => _ui.Prompt("User name (blank for anonymous): ", true), ct) ?? throw new OperationCanceledException();
        if (string.IsNullOrWhiteSpace(_user)) _user = "anonymous";
        string? password = SecretStore.Unprotect(_profile.Password);
        if (password == null)
        {
            password = _user == "anonymous" ? "fluentty@" : await Task.Run(() => _ui.Prompt($"{_user}@{_profile.Host}'s password: ", false), ct);
            if (password == null) throw new OperationCanceledException();
        }

        // "Auto" means: encrypt (explicit FTPS) and only fall back to plaintext after the user agrees.
        // FluentFTP's own Auto mode falls back silently, which lets anyone on the path strip AUTH TLS and
        // read the password — never use it. Security review M4 — see CLAUDE.md "Security invariants".
        var choice = _profile.FtpEncryption ?? _settings.Files.FtpEncryption;
        var mode = choice switch
        {
            FtpEncryption.None => FtpEncryptionMode.None,
            FtpEncryption.Implicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.Explicit,
        };
        try
        {
            _client = await ConnectWith(mode, password, ct);
        }
        catch (Exception ex) when (choice == FtpEncryption.Auto && IsTlsUnavailable(ex))
        {
            bool anonymous = _user == "anonymous" || _user == "ftp";
            if (!anonymous && !_ui.Confirm("Connection won't be encrypted",
                    $"{_profile.Host} doesn't support encrypted FTP (FTPS). If you continue, your user name, password and files " +
                    "are sent over the network as plain text, where anyone along the way can read them.\n\n" +
                    "Continue without encryption? (SFTP is the safer choice if the server offers it.)",
                    "Continue unencrypted", "Cancel"))
                throw new OperationCanceledException("Cancelled: the server doesn't support encryption.");
            _ui.Status("The server doesn't support FTPS; continuing unencrypted");
            _client = await ConnectWith(FtpEncryptionMode.None, password, ct);
        }
        _ui.Status($"Connected ({_client.ServerType}, {(_client.IsEncrypted ? "encrypted" : "not encrypted")})");
    }

    private static bool IsTlsUnavailable(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is FluentFTP.Exceptions.FtpSecurityNotAvailableException) return true;
        return false;
    }

    private async Task<AsyncFtpClient> ConnectWith(FtpEncryptionMode mode, string password, CancellationToken ct)
    {
        var files = _settings.Files;
        var config = new FtpConfig
        {
            EncryptionMode = mode,
            DataConnectionType = files.FtpDataMode switch
            {
                FtpDataMode.Passive => FtpDataConnectionType.PASV,
                FtpDataMode.ExtendedPassive => FtpDataConnectionType.EPSV,
                FtpDataMode.Active => FtpDataConnectionType.AutoActive,
                _ => FtpDataConnectionType.AutoPassive,
            },
            ConnectTimeout = Math.Max(3, _settings.Connection.ConnectTimeoutSeconds) * 1000,
        };
        var client = new AsyncFtpClient(_profile.Host, _user, password, _profile.EffectivePort, config);
        client.Encoding = TerminalEncoding(files.FtpEncoding);
        client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == SslPolicyErrors.None) { e.Accept = true; return; }
            e.Accept = files.Certificates switch
            {
                CertificatePolicy.AcceptAll => true,
                CertificatePolicy.Strict => false,
                _ => _ui.Confirm("Untrusted certificate",
                    $"The server's TLS certificate could not be verified:\n{e.PolicyErrorMessage}\n\nSubject: {e.Certificate.Subject}\nIssuer: {e.Certificate.Issuer}\n\nConnect anyway?",
                    "Connect", "Cancel"),
            };
        };
        _ui.Status($"Connecting to {_profile.Host}:{_profile.EffectivePort}{(mode == FtpEncryptionMode.None ? " (unencrypted)" : "")}");
        try
        {
            await client.Connect(ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    private static System.Text.Encoding TerminalEncoding(string name) => Terminal.TerminalConfig.GetEncoding(name);

    private AsyncFtpClient Client => _client ?? throw new InvalidOperationException("Not connected");

    private async Task<T> Run<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await work(); }
        finally { _gate.Release(); }
    }

    private Task Run(Func<Task> work, CancellationToken ct) => Run(async () => { await work(); return 0; }, ct);

    public Task<string> GetHomeAsync(CancellationToken ct) => Run(() => Client.GetWorkingDirectory(ct), ct);

    public Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run(async () =>
        (await Client.GetListing(path, ct)).Select(ToEntry).ToList(), ct);

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Run(async () =>
    {
        var info = await Client.GetObjectInfo(path, false, ct);
        return info == null ? null : ToEntry(info);
    }, ct);

    private static RemoteEntry ToEntry(FtpListItem i)
    {
        bool dir = i.Type == FtpObjectType.Directory;
        bool link = i.Type == FtpObjectType.Link;
        int? mode = i.Chmod > 0 ? Convert.ToInt32(i.Chmod.ToString(), 8) : null;
        return new RemoteEntry
        {
            Name = i.Name,
            FullPath = i.FullName,
            IsDirectory = dir || (link && i.LinkObject?.Type == FtpObjectType.Directory),
            IsSymlink = link,
            Size = Math.Max(0, i.Size),
            Modified = i.Modified == DateTime.MinValue ? null : i.Modified,
            Mode = mode,
            Permissions = mode.HasValue ? RemoteEntry.ModeString(mode.Value, dir, link) : i.RawPermissions ?? "",
            Owner = i.RawOwner ?? "",
        };
    }

    public Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct) => Run(async () =>
    {
        var p = progress == null ? null : new Progress<FtpProgress>(x => progress.Report(x.TransferredBytes));
        if (!await Client.DownloadStream(destination, remotePath, 0, p, ct)) throw new IOException($"Download of {remotePath} failed");
    }, ct);

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct) => Run(async () =>
    {
        var p = progress == null ? null : new Progress<FtpProgress>(x => progress.Report(x.TransferredBytes));
        var status = await Client.UploadStream(source, remotePath, FtpRemoteExists.Overwrite, false, p, ct);
        if (status == FtpStatus.Failed) throw new IOException($"Upload of {remotePath} failed");
    }, ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => Run(async () =>
    {
        if (entry.IsDirectory && !entry.IsSymlink) await Client.DeleteDirectory(entry.FullPath, ct);
        else await Client.DeleteFile(entry.FullPath, ct);
    }, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(() => Client.Rename(from, to, ct), ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(() => Client.CreateDirectory(path, ct), ct);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) =>
        Run(() => Client.SetFilePermissions(path, int.Parse(Convert.ToString(mode, 8)), ct), ct);

    public void Dispose()
    {
        var c = _client;
        _client = null;
        if (c == null) return;
        Task.Run(async () => { try { await c.Disconnect(); } catch { } c.Dispose(); });
    }
}
