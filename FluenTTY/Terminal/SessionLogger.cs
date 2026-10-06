using System.Text;
using FluenTTY.Models;

namespace FluenTTY.Terminal;

/// <summary>
/// Session log (PuTTY's Session › Logging): printable output or all raw output, file name with
/// &amp;H host, &amp;P port, &amp;Y &amp;M &amp;D date, &amp;T time, &amp;S session name placeholders.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly FileStream _stream;
    private readonly bool _flush;
    private readonly bool _timestamps;
    private bool _atLineStart = true;
    private readonly object _gate = new();
    public LogMode Mode { get; }
    public string Path { get; }

    private SessionLogger(FileStream stream, string path, LogMode mode, bool flush, bool timestamps)
    {
        _stream = stream;
        Path = path;
        Mode = mode;
        _flush = flush;
        _timestamps = timestamps;
    }

    public static string ExpandFileName(string pattern, SessionProfile profile)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        string p = Environment.ExpandEnvironmentVariables(pattern);
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] == '&' && i + 1 < p.Length)
            {
                char c = p[++i];
                sb.Append(c switch
                {
                    'H' => Sanitize(profile.Host),
                    'P' => profile.EffectivePort.ToString(),
                    'Y' => now.ToString("yyyy"),
                    'M' => now.ToString("MM"),
                    'D' => now.ToString("dd"),
                    'T' => now.ToString("HHmmss"),
                    'S' => Sanitize(string.IsNullOrEmpty(profile.Name) ? profile.Host : profile.Name),
                    '&' => "&",
                    _ => "&" + c,
                });
            }
            else sb.Append(p[i]);
        }
        return sb.ToString();
    }

    private static string Sanitize(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>Opens the log. <paramref name="askOverwrite"/> is called when the file exists and the setting is Ask.</summary>
    public static SessionLogger? Open(LoggingSettings settings, LogMode mode, SessionProfile profile, Func<string, bool?> askOverwrite)
    {
        if (mode == LogMode.Off) return null;
        var path = ExpandFileName(settings.FileName, profile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        bool append = settings.WhenExists == LogFileExists.Append;
        if (File.Exists(path) && settings.WhenExists == LogFileExists.Ask)
        {
            var answer = askOverwrite(path); // true = overwrite, false = append, null = don't log
            if (answer == null) return null;
            append = answer == false;
        }
        var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        var logger = new SessionLogger(fs, path, mode, settings.Flush, settings.Timestamps);
        if (settings.Header)
        {
            var header = $"=~=~=~=~=~=~=~=~=~=~=~= FluenTTY log {DateTime.Now:yyyy.MM.dd HH:mm:ss} =~=~=~=~=~=~=~=~=~=~=~=\r\n";
            logger.WriteRaw(Encoding.UTF8.GetBytes(header));
        }
        return logger;
    }

    public void WriteRaw(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _stream.Write(data);
            if (_flush) _stream.Flush();
        }
    }

    public void WriteText(string text)
    {
        if (!_timestamps)
        {
            WriteRaw(Encoding.UTF8.GetBytes(text));
            return;
        }
        var sb = new StringBuilder(text.Length + 32);
        foreach (var ch in text)
        {
            if (_atLineStart && ch != '\r' && ch != '\n')
            {
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                _atLineStart = false;
            }
            sb.Append(ch);
            if (ch == '\n') _atLineStart = true;
        }
        WriteRaw(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _stream.Flush(); } catch { }
            _stream.Dispose();
        }
    }
}
