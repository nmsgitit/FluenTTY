namespace FluenTTY.Files;

/// <summary>One row in the file browser.</summary>
public sealed class RemoteEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string Permissions { get; init; } = "";
    public int? Mode { get; init; }
    public string Owner { get; init; } = "";

    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Glyph => IsDirectory ? "" : IsSymlink ? "" : "";
    public bool IsParentLink => Name == "..";

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>"drwxr-xr-x" from a Unix mode.</summary>
    public static string ModeString(int mode, bool dir, bool link)
    {
        var c = new char[10];
        c[0] = link ? 'l' : dir ? 'd' : '-';
        string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++) c[i + 1] = (mode & (1 << (8 - i))) != 0 ? rwx[i] : '-';
        return new string(c);
    }
}

/// <summary>A remote file tree the browser and editor can work with (SFTP or FTP).</summary>
public interface IRemoteFileSystem : IDisposable
{
    string DisplayName { get; }
    bool IsConnected { get; }
    bool SupportsPermissions { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<string> GetHomeAsync(CancellationToken ct);
    Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct);
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);
    Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct);
    Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct);
    Task DeleteAsync(RemoteEntry entry, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task SetPermissionsAsync(string path, int mode, CancellationToken ct);
}

public static class RemotePath
{
    public static string Combine(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return "/" + name;
        return dir.EndsWith('/') ? dir + name : dir + "/" + name;
    }

    public static string Parent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var t = path.TrimEnd('/');
        int i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    public static string FileName(string path)
    {
        var t = path.TrimEnd('/');
        int i = t.LastIndexOf('/');
        return i < 0 ? t : t[(i + 1)..];
    }
}
