using FluenTTY.Connections;
using FluenTTY.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FluenTTY.Files;

/// <summary>SFTP through SSH.NET. Reuses the terminal tab's credentials so the side browser opens without new prompts.</summary>
public sealed class SftpFileSystem : IRemoteFileSystem
{
    private readonly SessionProfile _profile;
    private readonly AppSettings _settings;
    private readonly SshCredentials _creds;
    private readonly IConnectionUi _ui;
    private SftpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SftpFileSystem(SessionProfile profile, AppSettings settings, SshCredentials creds, IConnectionUi ui)
    {
        _profile = profile;
        _settings = settings;
        _creds = creds;
        _ui = ui;
    }

    public string DisplayName => $"sftp://{_creds.Username}@{_profile.HostWithPort}";
    public bool IsConnected => _client?.IsConnected == true;
    public bool SupportsPermissions => true;

    public Task ConnectAsync(CancellationToken ct) => Task.Run(() =>
    {
        _client?.Dispose();
        _client = SshConnector.Connect(info => new SftpClient(info), _profile, _settings, _ui, _creds, ct);
        int keepalive = _profile.KeepaliveSeconds ?? _settings.Connection.KeepaliveSeconds;
        if (keepalive > 0) _client.KeepAliveInterval = TimeSpan.FromSeconds(keepalive);
    }, ct);

    private SftpClient Client => _client ?? throw new InvalidOperationException("Not connected");

    /// <summary>SFTP requests on one client are serialized; SSH.NET multiplexes them but ordering keeps progress sane.</summary>
    private async Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await Task.Run(work, ct); }
        finally { _gate.Release(); }
    }

    private Task Run(Action work, CancellationToken ct) => Run(() => { work(); return 0; }, ct);

    public Task<string> GetHomeAsync(CancellationToken ct) => Run(() => Client.WorkingDirectory, ct);

    public Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct) => Run(() =>
        Client.ListDirectory(path).Where(f => f.Name != ".").Select(ToEntry).ToList(), ct);

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Run(() =>
    {
        if (!Client.Exists(path)) return (RemoteEntry?)null;
        return ToEntry(Client.Get(path));
    }, ct);

    private static RemoteEntry ToEntry(ISftpFile f)
    {
        var a = f.Attributes;
        int mode = (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
                 | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
                 | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);
        return new RemoteEntry
        {
            Name = f.Name,
            FullPath = f.FullName,
            IsDirectory = f.IsDirectory,
            IsSymlink = f.IsSymbolicLink,
            Size = f.Length,
            Modified = f.LastWriteTime,
            Mode = mode,
            Permissions = RemoteEntry.ModeString(mode, f.IsDirectory, f.IsSymbolicLink),
            Owner = $"{a.UserId}:{a.GroupId}",
        };
    }

    public Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
        Run(() => Client.DownloadFile(remotePath, destination, done => progress?.Report((long)done)), ct);

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct) =>
        Run(() => Client.UploadFile(source, remotePath, true, done => progress?.Report((long)done)), ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct) => Run(() => DeleteRecursive(entry, ct), ct);

    private void DeleteRecursive(RemoteEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.IsDirectory && !entry.IsSymlink)
        {
            foreach (var child in Client.ListDirectory(entry.FullPath).Where(f => f.Name is not "." and not ".."))
                DeleteRecursive(ToEntry(child), ct);
            Client.DeleteDirectory(entry.FullPath);
        }
        else Client.DeleteFile(entry.FullPath);
    }

    public Task RenameAsync(string from, string to, CancellationToken ct) => Run(() => Client.RenameFile(from, to), ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(() => Client.CreateDirectory(path), ct);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Run(() => Client.ChangePermissions(path, (short)mode), ct);

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
        _client = null;
    }
}
