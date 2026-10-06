using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FluenTTY.Services;

/// <summary>A problem the user should read (shown as is in the update bar).</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Updates from GitHub Releases. At startup (Settings › Updates) FluenTTY asks GitHub's public API for the
/// latest release of <see cref="Repository"/>; nothing else is sent. When the user agrees, it downloads the
/// matching file — the setup for installed copies, the exe for portable ones — checks its SHA-256 against
/// the release (GitHub's asset digest, else the release's SHA256SUMS.txt), backs up the data folder and
/// restarts into the new version. User data is never touched: the installer upgrades the program folder
/// only (data is in %AppData%\FluenTTY), the portable update swaps only the exe (data is in
/// "FluenTTY Data" beside it). Anything that can't be done automatically falls back to the releases page.
/// </summary>
public static class Updater
{
    /// <summary>
    /// owner/name on GitHub. A constant on purpose: settings (which can be imported) must never be able to
    /// point the updater somewhere else.
    /// </summary>
    public const string Repository = "nmsgitit/FluenTTY";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

    public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
    {
        public string Number => Version.ToString(3);
        public ReleaseAsset? Setup => Find($"FluenTTY-Setup-{Number}.exe");
        public ReleaseAsset? Portable => Find($"FluenTTY-Portable-{Number}.exe");
        public ReleaseAsset? Checksums => Find("SHA256SUMS.txt");
        /// <summary>The file this copy of FluenTTY updates itself with.</summary>
        public ReleaseAsset? ForThisCopy => AppInfo.IsPortable ? Portable : Setup;
        private ReleaseAsset? Find(string name) => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The running version, as major.minor.patch.</summary>
    public static Version Current { get; } = Normalize(PretendVersion() ?? Version.Parse(AppInfo.Version));

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? PretendVersion()
    {
#if DEBUG
        // Testing aid (Debug builds only): FLUENTTY_PRETEND_VERSION=1.0.0 makes the published release look new.
        return Version.TryParse(Environment.GetEnvironmentVariable("FLUENTTY_PRETEND_VERSION"), out var v) ? v : null;
#else
        return null;
#endif
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Follows GitHub's redirect to its download host; HttpClient never follows https → http.
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FluenTTY/{AppInfo.Version}");
        return http;
    }

    // ================================================================== checking

    /// <summary>
    /// The newest published release if it is newer than this copy (and, unless <paramref name="manual"/>,
    /// not one the user chose to skip); null when up to date. Throws on network errors.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync(bool manual, CancellationToken ct)
    {
        var latest = await GetLatestAsync(ct);
        var state = LoadState();
        state.LastCheckUtc = DateTime.UtcNow;
        SaveState(state);
        if (latest == null || latest.Version <= Current) return null;
        if (!manual && state.SkippedVersion == latest.Number) return null;
        Diagnostics.Log($"update: {latest.Number} available (running {Current.ToString(3)})");
        return latest;
    }

    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var resp = await Http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(cts.Token), Repository);
    }

    /// <summary>
    /// Reads GitHub's release JSON. Drafts, pre-releases and tags that aren't versions are ignored, and so
    /// are links that don't point into the repository's own releases.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string json, string repository)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Flag(r, "draft") || Flag(r, "prerelease")) return null;
        var tag = Text(r, "tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string releases = $"https://github.com/{repository}/releases/";
        var page = Text(r, "html_url");
        if (page == null || !page.StartsWith(releases, StringComparison.OrdinalIgnoreCase)) page = releases.TrimEnd('/');

        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Text(a, "name");
                var url = Text(a, "browser_download_url");
                if (name == null || url == null || !url.StartsWith(releases + "download/", StringComparison.OrdinalIgnoreCase)) continue;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var digest = Text(a, "digest");
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(name, url, size, sha));
            }
        }
        return new ReleaseInfo(Normalize(version), tag, Text(r, "body") ?? "", page, assets);

        static bool Flag(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        static string? Text(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static void Skip(ReleaseInfo release)
    {
        var state = LoadState();
        state.SkippedVersion = release.Number;
        SaveState(state);
    }

    public static void OpenReleasePage(ReleaseInfo? release = null)
    {
        try { Process.Start(new ProcessStartInfo(release?.PageUrl ?? ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostics.Log($"update: open page failed: {ex.Message}"); }
    }

    // ================================================================== installing

    /// <summary>Whether this copy can update itself; otherwise <paramref name="reason"/> says why (and the page is offered).</summary>
    public static bool CanInstall(ReleaseInfo release, out string reason)
    {
        reason = "";
        if (release.ForThisCopy == null)
        {
            reason = "This release has no download for your copy of FluenTTY.";
            return false;
        }
        if (AppInfo.IsPortable)
        {
            if (AppInfo.TryWritable(AppInfo.ExeDirectory)) return true;
            reason = "FluenTTY can't write to the folder it runs from.";
            return false;
        }
        if (File.Exists(Path.Combine(AppInfo.ExeDirectory, "unins000.exe"))) return true;
        reason = "This copy wasn't installed with FluenTTY's setup.";
        return false;
    }

    /// <summary>Installed for all users (Program Files): the setup needs Windows' permission prompt.</summary>
    public static bool NeedsElevation => !AppInfo.IsPortable && !AppInfo.TryWritable(AppInfo.ExeDirectory);

    /// <summary>
    /// Downloads this copy's file from the release and verifies it; returns the verified file. A portable
    /// update lands next to the exe (same drive, so the swap is a rename), a setup in %TEMP%.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        var asset = release.ForThisCopy ?? throw new UpdateException("This release has no download for your copy of FluenTTY.");
        var expected = asset.Sha256 ?? await ChecksumFromListAsync(release, asset, ct)
                       ?? throw new UpdateException("The release has no checksum to verify the download with.");

        string target = AppInfo.IsPortable
            ? AppInfo.ExePath + ".update"
            : Path.Combine(UpdatesFolder, asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        try
        {
            using var resp = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await input.ReadAsync(buf, ct)) > 0)
                {
                    await output.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (asset.Size > 0 && done != asset.Size)
                throw new UpdateException("The download was incomplete. Please try again.");
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (actual != expected)
                throw new UpdateException("The downloaded file didn't match the release's checksum, so it wasn't used.");
            File.Move(part, target, overwrite: true);
            Diagnostics.Log($"update: downloaded and verified {asset.Name} ({done:N0} bytes)");
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    private static async Task<string?> ChecksumFromListAsync(ReleaseInfo release, ReleaseAsset asset, CancellationToken ct)
    {
        if (release.Checksums is not { } sums) return null;
        var text = await Http.GetStringAsync(sums.DownloadUrl, ct);
        return FindChecksum(text, asset.Name);
    }

    /// <summary>Reads "&lt;sha256&gt;  &lt;file name&gt;" lines (sha256sum / Get-FileHash output).</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public static string UpdatesFolder => Path.Combine(Path.GetTempPath(), "FluenTTY Updates");

    /// <summary>
    /// Last step, called while FluenTTY closes (after the user agreed and every tab let go): saves and backs
    /// up the user's data, then starts the setup or the new portable exe, which waits for this process to
    /// exit. Throws (and changes nothing) if the backup or the start fails.
    /// </summary>
    public static void Apply(string file, ReleaseInfo release)
    {
        SettingsStore.FlushAll();
        BackupUserData($"update to {release.Number}");
        if (AppInfo.IsPortable) ApplyPortable(file);
        else ApplySetup(file);
    }

    private static void ApplySetup(string setup)
    {
        // Same install mode as before; the setup waits for this copy to exit (installer\FluenTTY.iss,
        // /FLUENTTYUPDATE) and starts the new version afterwards. Its own uninstall data and the user's
        // %AppData%\FluenTTY are left alone, so nothing but the program files changes.
        var args = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FLUENTTYUPDATE=1 {(NeedsElevation ? "/ALLUSERS" : "/CURRENTUSER")}";
        Diagnostics.Log($"update: starting {setup} {args}");
        Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = true })?.Dispose();
    }

    private static void ApplyPortable(string newExe)
    {
        // A running exe can be renamed but not overwritten: move it aside, put the new one in its place
        // (keeping the file name, so shortcuts and pins keep working) and start it. It waits for this process
        // to exit, then deletes the old file. The "FluenTTY Data" folder beside it is never touched.
        var exe = AppInfo.ExePath;
        var old = exe + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { old = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old"; }
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppInfo.ExeDirectory };
            psi.ArgumentList.Add(RelaunchFlag);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(old);
            Process.Start(psi)?.Dispose();
            Diagnostics.Log($"update: portable exe replaced, old copy at {old}");
        }
        catch
        {
            // Put everything back the way it was.
            try { if (File.Exists(exe)) File.Move(exe, newExe, overwrite: true); } catch { }
            try { File.Move(old, exe); } catch { }
            throw;
        }
    }

    public const string RelaunchFlag = "--after-update";

    /// <summary>
    /// First thing at startup: a portable copy started by <see cref="ApplyPortable"/> waits for the old
    /// process to finish saving and exit, then removes the old exe. Returns the remaining arguments.
    /// </summary>
    public static string[] HandleRelaunch(string[] args)
    {
        if (args.Length < 3 || args[0] != RelaunchFlag) return args;
        if (int.TryParse(args[1], out int pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(30_000); } catch { }
        }
        var old = args[2];
        // Only ever the file this exe was renamed from.
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(old)), AppInfo.ExeDirectory, StringComparison.OrdinalIgnoreCase) &&
            old.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { Thread.Sleep(250); }
            }
        }
        return args[3..];
    }

    // ================================================================== user data safety

    /// <summary>
    /// Copies settings, sessions, host keys and window state to Backups\&lt;date&gt; in the data folder and
    /// keeps the newest five. Fails loudly: no backup, no update.
    /// </summary>
    public static string BackupUserData(string reason)
    {
        var root = Path.Combine(AppInfo.DataDirectory, "Backups");
        var dir = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd HHmmss} before {SafeName(reason)}");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(AppInfo.DataDirectory, "*.json"))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        foreach (var stale in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(5))
            try { Directory.Delete(stale, true); } catch { }
        Diagnostics.Log($"update: user data backed up to {dir}");
        return dir;
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Leftover downloads from earlier updates.</summary>
    public static void CleanUpDownloads()
    {
        try { if (Directory.Exists(UpdatesFolder)) Directory.Delete(UpdatesFolder, true); } catch { }
        if (AppInfo.IsPortable)
            foreach (var f in new[] { AppInfo.ExePath + ".update", AppInfo.ExePath + ".update.part" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    // ================================================================== what's new

    /// <summary>
    /// Called once per start: notes the running version and returns the change-log entries the user hasn't
    /// seen since the version they ran last (null when nothing is new, on a first run, or when turned off).
    /// </summary>
    public static List<Changelog.Entry>? TakeWhatsNew()
    {
        var state = LoadState();
        Version? last = Version.TryParse(state.LastRunVersion, out var v) ? Normalize(v) : null;
        state.LastRunVersion = Current.ToString(3);
        SaveState(state);

        if (!SettingsStore.Settings.Interface.ShowWhatsNew) return null;
        List<Changelog.Entry> entries;
        if (last == null)
        {
            // Versions before 1.2.0 didn't record this. A brand-new user (welcome guide not done yet) has
            // nothing to catch up on; someone who already used FluenTTY sees this version's notes.
            if (!SettingsStore.Settings.Interface.OnboardingDone) return null;
            entries = Changelog.All.Where(e => e.Version == Current).ToList();
        }
        else if (last < Current) entries = Changelog.Between(last, Current);
        else return null;
        return entries.Count > 0 ? entries : null;
    }

    // ================================================================== state (not part of settings exports)

    private sealed class State
    {
        public string? LastRunVersion { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTime? LastCheckUtc { get; set; }
    }

    private static string StatePath => Path.Combine(AppInfo.DataDirectory, "update-state.json");

    private static State LoadState() => Json.Load<State>(StatePath) ?? new State();

    private static void SaveState(State s)
    {
        try { Json.Save(StatePath, s); } catch (Exception ex) { Diagnostics.Log($"update: save state failed: {ex.Message}"); }
    }

    public static DateTime? LastCheckUtc => LoadState().LastCheckUtc;
}
