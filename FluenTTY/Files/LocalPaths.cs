using System.Text;

namespace FluenTTY.Files;

/// <summary>
/// The only place remote names become local paths. A server chooses every name it lists, so a name is
/// untrusted input: it may contain "..\", a rooted "C:\..." path, ':' (alternate data streams), device
/// names, control or bidi-override characters. Security review H1/M1 — see CLAUDE.md "Security invariants".
/// </summary>
public static class LocalPaths
{
    public const int MaxTreeDepth = 32;
    public const int MaxTreeFiles = 20_000;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars());

    /// <summary>Unicode characters that reorder or hide text ("invoice‮txt.exe" shows as "invoiceexe.txt").</summary>
    public static bool IsBidiOrInvisible(char c) =>
        c is '\u061C' or '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF'
        || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

    /// <summary>A single, harmless local file name for a remote name (never a path).</summary>
    public static string SafeFileName(string remoteName)
    {
        var name = remoteName ?? "";
        int cut = name.LastIndexOfAny(new[] { '/', '\\' });
        if (cut >= 0) name = name[(cut + 1)..];
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (IsBidiOrInvisible(ch)) continue;
            sb.Append(char.IsControl(ch) || Invalid.Contains(ch) ? '_' : ch);
        }
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name == "." || name == "..") name = "_";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem)) name = "_" + name;
        if (name.Length > 200)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(200 - ext.Length)] + ext;
        }
        return name;
    }

    public static bool IsInside(string path, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>folder\SafeFileName(remoteName), verified to stay inside <paramref name="folder"/>.</summary>
    public static string ChildPath(string folder, string remoteName)
    {
        var baseDir = Path.GetFullPath(folder);
        var full = Path.GetFullPath(Path.Combine(baseDir, SafeFileName(remoteName)));
        if (!IsInside(full, baseDir))
            throw new IOException($"Refused to write \"{remoteName}\" outside {baseDir}.");
        return full;
    }

    /// <summary>Extensions Windows runs, installs, mounts or follows when "opened".</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".btm", ".scr", ".pif", ".cpl", ".msc", ".msi", ".msp", ".mst", ".msix", ".msixbundle",
        ".appx", ".appxbundle", ".appinstaller", ".application", ".appref-ms", ".xbap", ".vsix", ".vsto", ".gadget",
        ".ps1", ".psm1", ".psd1", ".ps1xml", ".psc1", ".pssc", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml",
        ".vbs", ".vbe", ".vb", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".ws", ".sct", ".hta", ".jar",
        ".lnk", ".url", ".website", ".scf", ".shb", ".shs", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms",
        ".reg", ".inf", ".ins", ".isp", ".chm", ".hlp", ".dll", ".ocx", ".sys", ".drv", ".cab", ".xll", ".mof",
        ".diagcab", ".diagcfg", ".diagpkg", ".theme", ".themepack", ".desktopthemepackfile",
        ".iso", ".img", ".vhd", ".vhdx",
    };

    public static bool IsRunnable(string fileName) => Runnable.Contains(Path.GetExtension(fileName.TrimEnd('.', ' ')));

    /// <summary>
    /// Mark-of-the-Web: tells SmartScreen, Office Protected View and script hosts the file came from
    /// the network. Fails silently on file systems without alternate data streams (FAT, network shares).
    /// </summary>
    public static void MarkFromInternet(string path, string sourceUrl)
    {
        // The URL contains server-chosen names: drop control/bidi characters (a line break would add
        // lines to the Zone.Identifier file) and percent-encode everything outside printable ASCII.
        var url = new StringBuilder();
        foreach (var ch in sourceUrl)
        {
            if (char.IsControl(ch) || IsBidiOrInvisible(ch)) continue;
            if (ch >= 0x20 && ch <= 0x7E) url.Append(ch);
            else foreach (var b in Encoding.UTF8.GetBytes(ch.ToString())) url.Append('%').Append(b.ToString("X2"));
        }
        try { File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={url}\r\n", Encoding.ASCII); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>
    /// Downloads a remote file or folder tree into <paramref name="folder"/>. Every local path goes
    /// through <see cref="ChildPath"/>; symlinked folders are not followed; nesting, total files and
    /// already-visited folders are bounded so a hostile server can't loop us or fill the disk.
    /// </summary>
    /// <param name="downloadFile">Downloads one remote file to a checked local path (UI supplies progress/overwrite prompts).</param>
    /// <param name="skipped">Receives a note for everything that was not downloaded.</param>
    public static async Task DownloadTree(IRemoteFileSystem fs, RemoteEntry entry, string folder,
        Func<RemoteEntry, string, Task> downloadFile, List<string> skipped, CancellationToken ct)
    {
        int files = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        async Task Walk(RemoteEntry e, string localDir, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsParentLink || e.Name is "." or "..") return;
            string local;
            try { local = ChildPath(localDir, e.Name); }
            catch (IOException ex) { skipped.Add(ex.Message); return; }

            if (!e.IsDirectory)
            {
                if (++files > MaxTreeFiles) { if (files == MaxTreeFiles + 1) skipped.Add($"Stopped after {MaxTreeFiles:N0} files."); return; }
                await downloadFile(e, local);
                return;
            }
            if (e.IsSymlink) { skipped.Add($"{e.FullPath}: symbolic link to a folder, not followed."); return; }
            if (depth >= MaxTreeDepth) { skipped.Add($"{e.FullPath}: nested more than {MaxTreeDepth} folders deep, not downloaded."); return; }
            if (!visited.Add(e.FullPath.TrimEnd('/'))) { skipped.Add($"{e.FullPath}: already downloaded (folder loop)."); return; }

            Directory.CreateDirectory(local);
            foreach (var child in await fs.ListAsync(e.FullPath, ct))
                await Walk(child, local, depth + 1);
        }

        await Walk(entry, folder, 0);
    }
}
