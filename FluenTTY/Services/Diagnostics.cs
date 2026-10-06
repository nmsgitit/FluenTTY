using System.IO;

namespace FluenTTY.Services;

/// <summary>
/// Opt-in diagnostic log: set FLUENTTY_LOG=&lt;file&gt; before launching. Nothing is written otherwise.
/// Never log passwords, keys or session data here.
/// </summary>
public static class Diagnostics
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("FLUENTTY_LOG");
    private static readonly object Gate = new();

    public static bool Enabled => !string.IsNullOrEmpty(Path);

    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(Path)) return;
        try
        {
            lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
