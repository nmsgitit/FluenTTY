using System.IO;
using System.Reflection;

namespace FluenTTY.Services;

/// <summary>
/// The user-facing change log: CHANGELOG.md at the repository root, embedded in the exe. Format:
/// "## 1.2.0 — 6 October 2026" headings followed by "- " bullet lines; everything else is ignored.
/// The same text becomes the GitHub release notes (tools\publish-release.ps1).
/// </summary>
public static class Changelog
{
    public sealed record Entry(Version Version, string Date, IReadOnlyList<string> Items);

    private static IReadOnlyList<Entry>? _all;

    /// <summary>Every version, newest first.</summary>
    public static IReadOnlyList<Entry> All => _all ??= Load();

    private static IReadOnlyList<Entry> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return Array.Empty<Entry>();
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"changelog: {ex.Message}");
            return Array.Empty<Entry>();
        }
    }

    public static List<Entry> Parse(string markdown)
    {
        var entries = new List<Entry>();
        Version? version = null;
        string date = "";
        var items = new List<string>();
        bool inComment = false;

        void Flush()
        {
            if (version != null) entries.Add(new Entry(version, date, items.ToList()));
            items.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (inComment) { if (line.Contains("-->")) inComment = false; continue; }
            if (line.StartsWith("<!--")) { inComment = !line.Contains("-->"); continue; }
            if (line.StartsWith("## "))
            {
                Flush();
                var head = line[3..].Trim();
                int split = head.IndexOfAny(new[] { ' ', '—', '-' });
                var number = split < 0 ? head : head[..split];
                version = System.Version.TryParse(number.TrimStart('v', 'V'), out var v) ? v : null;
                date = split < 0 ? "" : head[split..].Trim(' ', '—', '-');
            }
            else if (version != null && (line.StartsWith("- ") || line.StartsWith("* ")))
                items.Add(line[2..].Trim());
        }
        Flush();
        return entries.OrderByDescending(e => e.Version).ToList();
    }

    /// <summary>Versions after <paramref name="since"/> up to and including <paramref name="upTo"/>, newest first.</summary>
    public static List<Entry> Between(Version since, Version upTo) =>
        All.Where(e => e.Version > since && e.Version <= upTo).ToList();
}
