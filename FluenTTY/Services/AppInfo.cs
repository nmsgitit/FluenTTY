using System.IO;
using System.Reflection;

namespace FluenTTY.Services;

/// <summary>Build flavour, version and where FluenTTY keeps its files.</summary>
public static class AppInfo
{
#if PORTABLE
    public static readonly bool IsPortable = true;
#else
    public static readonly bool IsPortable = false;
#endif

    public const string Name = "FluenTTY";

    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Flavour => IsPortable ? "Portable" : "Installed";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FluenTTY.exe");

    public static string ExeDirectory => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    private static string? _dataDir;

    /// <summary>
    /// settings.json, sessions.json, known_hosts.json. Installed: %AppData%\FluenTTY. Portable: a
    /// "FluenTTY Data" folder next to the exe (falls back to %LocalAppData%\FluenTTY Portable if that
    /// folder isn't writable, e.g. when the exe runs from a read-only share).
    /// FLUENTTY_DATA_DIR overrides both — used for sandboxed testing.
    /// </summary>
    public static string DataDirectory => _dataDir ??= ResolveDataDirectory();

    public static bool DataDirectoryIsFallback { get; private set; }

    private static string ResolveDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("FLUENTTY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }

        if (IsPortable)
        {
            var beside = Path.Combine(ExeDirectory, "FluenTTY Data");
            if (TryWritable(beside)) return beside;
            DataDirectoryIsFallback = true;
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluenTTY Portable");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The data folder is on a network share (UNC path or mapped network drive). Anyone who can write
    /// there can change settings that run programs or trust host keys, so the portable copy warns.
    /// Security review M6 — see CLAUDE.md "Security invariants".
    /// </summary>
    public static bool DataDirectoryIsOnNetwork
    {
        get
        {
            try
            {
                var full = Path.GetFullPath(DataDirectory);
                if (full.StartsWith(@"\\")) return true;
                return new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network;
            }
            catch { return false; }
        }
    }

    /// <summary>Per-user (never shared) record of network data folders the user already acknowledged.</summary>
    private static string NetworkAckFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluenTTY Portable", "shared-data-acknowledged.txt");

    public static bool NetworkDataAcknowledged
    {
        get { try { return File.Exists(NetworkAckFile) && File.ReadAllLines(NetworkAckFile).Contains(DataDirectory, StringComparer.OrdinalIgnoreCase); } catch { return false; } }
        set
        {
            if (!value) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(NetworkAckFile)!); File.AppendAllLines(NetworkAckFile, new[] { DataDirectory }); } catch { }
        }
    }

    public static bool TryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Per-process scratch folder for files opened in the editor; removed on exit.</summary>
    public static string TempDirectory
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "FluenTTY", Environment.ProcessId.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void CleanupTemp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "FluenTTY");
            var mine = Path.Combine(root, Environment.ProcessId.ToString());
            if (Directory.Exists(mine)) Directory.Delete(mine, true);
            // Leftovers from crashed runs whose process is gone.
            if (Directory.Exists(root))
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    if (int.TryParse(Path.GetFileName(d), out int pid) && !ProcessExists(pid))
                        try { Directory.Delete(d, true); } catch { }
                }
            }
        }
        catch { }
    }

    private static bool ProcessExists(int pid)
    {
        try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); return true; }
        catch { return false; }
    }
}
