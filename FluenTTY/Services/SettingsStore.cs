using System.IO;
using System.Windows.Threading;
using FluenTTY.Models;

namespace FluenTTY.Services;

/// <summary>
/// The app's settings, saved sessions and known host keys, loaded once at startup and saved to
/// <see cref="AppInfo.DataDirectory"/>. Mutate on the UI thread, then call <see cref="NotifySettingsChanged"/>
/// (settings) or <see cref="SaveSessions"/> / <see cref="SaveHostKeys"/>.
/// </summary>
public static class SettingsStore
{
    public static AppSettings Settings { get; private set; } = new();
    public static SessionStoreData Sessions { get; private set; } = new();
    public static List<KnownHostKey> HostKeys { get; private set; } = new();

    public static string SettingsPath => Path.Combine(AppInfo.DataDirectory, "settings.json");
    public static string SessionsPath => Path.Combine(AppInfo.DataDirectory, "sessions.json");
    public static string HostKeysPath => Path.Combine(AppInfo.DataDirectory, "known_hosts.json");

    /// <summary>Raised on the UI thread after any settings change (live-applies fonts, colours, theme...).</summary>
    public static event Action? SettingsChanged;
    public static event Action? SessionsChanged;

    private static DispatcherTimer? _saveTimer;

    public static void Load()
    {
        Settings = Json.Load<AppSettings>(SettingsPath) ?? new AppSettings();
        Sessions = Json.Load<SessionStoreData>(SessionsPath) ?? new SessionStoreData();
        HostKeys = Json.Load<List<KnownHostKey>>(HostKeysPath) ?? new List<KnownHostKey>();
        Normalize(Settings);
    }

    /// <summary>Fills in anything a hand-edited or older file left empty.</summary>
    public static void Normalize(AppSettings s)
    {
        s.Interface ??= new(); s.Terminal ??= new(); s.Keyboard ??= new(); s.Bell ??= new();
        s.Appearance ??= new(); s.Selection ??= new(); s.Translation ??= new(); s.Logging ??= new();
        s.Connection ??= new(); s.Proxy ??= new(); s.Ssh ??= new(); s.Telnet ??= new(); s.Rlogin ??= new();
        s.Serial ??= new(); s.Rdp ??= new(); s.Vnc ??= new(); s.Xdmcp ??= new(); s.Files ??= new(); s.Editor ??= new();
        s.Appearance.CustomSchemes ??= new();
        s.Appearance.FontSize = Math.Clamp(s.Appearance.FontSize, 5, 72);
        s.Appearance.LineHeight = Math.Clamp(s.Appearance.LineHeight, 0.8, 2.0);
        s.Appearance.BackgroundOpacity = Math.Clamp(s.Appearance.BackgroundOpacity, 0, 100);
        s.Terminal.ScrollbackLines = Math.Clamp(s.Terminal.ScrollbackLines, 0, 1_000_000);
        s.Interface.ScreensaverMinutes = Math.Clamp(s.Interface.ScreensaverMinutes, 1, 240);
        s.Interface.ScreensaverCrabs = Math.Clamp(s.Interface.ScreensaverCrabs, 1, 30);
        s.Connection.Environment ??= new();
        s.Ssh.PortForwards ??= new(); s.Ssh.ManualHostKeys ??= new();
        s.Ssh.KexAlgorithms ??= new(); s.Ssh.HostKeyAlgorithms ??= new(); s.Ssh.Ciphers ??= new(); s.Ssh.Macs ??= new();
    }

    public static void NotifySettingsChanged()
    {
        SettingsChanged?.Invoke();
        ScheduleSave();
    }

    private static void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettingsNow(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveSettingsNow()
    {
        try { Json.Save(SettingsPath, Settings); }
        catch (Exception ex) { Diagnostics.Log($"save settings failed: {ex.Message}"); }
    }

    public static void ReplaceSettings(AppSettings settings)
    {
        Normalize(settings);
        Settings = settings;
        NotifySettingsChanged();
    }

    public static void SaveSessions()
    {
        try { Json.Save(SessionsPath, Sessions); }
        catch (Exception ex) { Diagnostics.Log($"save sessions failed: {ex.Message}"); }
        SessionsChanged?.Invoke();
    }

    public static void SaveHostKeys()
    {
        try { Json.Save(HostKeysPath, HostKeys); }
        catch (Exception ex) { Diagnostics.Log($"save host keys failed: {ex.Message}"); }
    }

    public static void FlushAll()
    {
        _saveTimer?.Stop();
        SaveSettingsNow();
        try { Json.Save(SessionsPath, Sessions); } catch { }
    }

    // ------------------------------------------------------------------ sessions

    public static void AddOrUpdateSession(SessionProfile profile)
    {
        var i = Sessions.Sessions.FindIndex(s => s.Id == profile.Id);
        if (i >= 0) Sessions.Sessions[i] = profile;
        else Sessions.Sessions.Add(profile);
        SaveSessions();
    }

    public static void DeleteSession(Guid id)
    {
        Sessions.Sessions.RemoveAll(s => s.Id == id);
        SaveSessions();
    }

    public static void NoteRecent(SessionProfile profile)
    {
        var copy = profile.Clone();
        copy.Password = null; // recents never keep passwords
        copy.LastUsed = DateTime.Now;
        Sessions.Recent.RemoveAll(r => r.Protocol == copy.Protocol && string.Equals(r.Host, copy.Host, StringComparison.OrdinalIgnoreCase)
                                       && r.Port == copy.Port && r.Username == copy.Username);
        Sessions.Recent.Insert(0, copy);
        if (Sessions.Recent.Count > 15) Sessions.Recent.RemoveRange(15, Sessions.Recent.Count - 15);

        var saved = Sessions.Sessions.FirstOrDefault(s => s.Id == profile.Id);
        if (saved != null) saved.LastUsed = DateTime.Now;
        SaveSessions();
    }

    // ------------------------------------------------------------------ host keys

    public static KnownHostKey? FindHostKey(string host, int port, string algorithm) =>
        HostKeys.FirstOrDefault(k => k.Port == port && k.Algorithm == algorithm && string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<KnownHostKey> HostKeysFor(string host, int port) =>
        HostKeys.Where(k => k.Port == port && string.Equals(k.Host, host, StringComparison.OrdinalIgnoreCase));

    public static void StoreHostKey(KnownHostKey key)
    {
        HostKeys.RemoveAll(k => k.Key.Equals(key.Key, StringComparison.OrdinalIgnoreCase));
        HostKeys.Add(key);
        SaveHostKeys();
    }

    // ------------------------------------------------------------------ colour schemes

    public static IEnumerable<ColorScheme> AllSchemes => ColorScheme.BuiltIns.Concat(Settings.Appearance.CustomSchemes);

    public static ColorScheme FindScheme(string? name, bool dark)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var hit = AllSchemes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return ColorScheme.BuiltIns.First(s => s.Name == (dark ? ColorScheme.DefaultDark : ColorScheme.DefaultLight));
    }
}
