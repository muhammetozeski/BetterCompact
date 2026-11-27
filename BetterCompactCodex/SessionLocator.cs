using System.Text;
using System.Text.Json;

namespace BetterCompactCodex;

/// <summary>
/// Locates Codex (ChatGPT) rollout .jsonl session files by path or UUID and resolves titles from session_index.jsonl.
/// </summary>
static class SessionLocator
{
    public static string DefaultCodexRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string DefaultSessionsFolder() =>
        Path.Combine(DefaultCodexRoot(), "sessions");

    public static string SessionIndexPath() =>
        Path.Combine(DefaultCodexRoot(), "session_index.jsonl");

    /// <summary>Loads thread_name titles indexed by session UUID from session_index.jsonl.</summary>
    public static Dictionary<string, (string Title, DateTimeOffset? UpdatedAt)> LoadSessionIndex()
    {
        Dictionary<string, (string Title, DateTimeOffset? UpdatedAt)> index = new(StringComparer.OrdinalIgnoreCase);
        string indexPath = SessionIndexPath();
        if (!File.Exists(indexPath)) return index;

        try
        {
            using FileStream stream = new(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    string? id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    string? name = root.TryGetProperty("thread_name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
                    string? updatedRaw = root.TryGetProperty("updated_at", out JsonElement updEl) && updEl.ValueKind == JsonValueKind.String ? updEl.GetString() : null;
                    DateTimeOffset? updatedAt = DateTimeOffset.TryParse(updatedRaw, out DateTimeOffset parsed) ? parsed.ToLocalTime() : null;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        index[id] = (name.Trim(), updatedAt);
                }
                catch { }
            }
        }
        catch { }

        return index;
    }

    /// <summary>Resolves a full path or session UUID to the matching Codex rollout .jsonl file.</summary>
    public static string ResolveSessionPath(string input)
    {
        string expanded = Environment.ExpandEnvironmentVariables(input).Trim('"');
        if (File.Exists(expanded))
            return Path.GetFullPath(expanded);

        string sessionsRoot = DefaultSessionsFolder();
        if (!Directory.Exists(sessionsRoot))
            throw new DirectoryNotFoundException($"Codex oturum klasörü bulunamadı: {sessionsRoot}");

        string? match = Directory.EnumerateFiles(sessionsRoot, $"*{expanded}*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (match != null) return match;
        throw new FileNotFoundException($"Codex oturumu bulunamadı: {input}");
    }

    /// <summary>Lists recent or title-matching Codex sessions.</summary>
    public static List<SessionSummaryItem> ListSessions(int limit, string? query = null)
    {
        string sessionsRoot = DefaultSessionsFolder();
        if (!Directory.Exists(sessionsRoot)) return [];

        Dictionary<string, (string Title, DateTimeOffset? UpdatedAt)> index = LoadSessionIndex();
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        int scanCap = hasQuery ? 600 : Math.Max(limit * 2, 40);

        IEnumerable<FileInfo> files = Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(scanCap);

        List<SessionSummaryItem> items = [];
        foreach (FileInfo file in files)
        {
            string id = ExtractUuidFromFileName(file.Name);
            string title = index.TryGetValue(id, out var meta) ? meta.Title : id;

            if (hasQuery &&
                !title.Contains(query!, StringComparison.OrdinalIgnoreCase) &&
                !id.Contains(query!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new SessionSummaryItem(
                id,
                title,
                new DateTimeOffset(file.LastWriteTime),
                string.Empty,
                file.FullName));

            if (items.Count >= limit) break;
        }

        return items;
    }

    static string ExtractUuidFromFileName(string fileName)
    {
        // Format: rollout-2026-11-24T14-14-55-<uuid>.jsonl
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length >= 36)
        {
            string tail = stem[^36..];
            if (Guid.TryParse(tail, out _)) return tail;
        }
        return stem;
    }
}
