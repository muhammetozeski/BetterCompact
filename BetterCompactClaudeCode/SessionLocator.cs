using System.Text;
using System.Text.Json;

namespace BetterCompactClaudeCode;

/// <summary>
/// Locates Claude Code .jsonl session files by path or UUID and lists/searches recent sessions.
/// </summary>
static class SessionLocator
{
    /// <summary>Returns the default Claude Code projects folder (%USERPROFILE%\.claude\projects).</summary>
    public static string DefaultProjectsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Resolves a full path or session UUID to the matching Claude Code .jsonl file.</summary>
    public static string ResolveSessionPath(string input)
    {
        string expanded = Environment.ExpandEnvironmentVariables(input).Trim('"');
        if (File.Exists(expanded))
            return Path.GetFullPath(expanded);

        string projectsRoot = DefaultProjectsFolder();
        if (!Directory.Exists(projectsRoot))
            throw new DirectoryNotFoundException($"Claude Code oturum klasörü bulunamadı: {projectsRoot}");

        string targetName = expanded.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(expanded)
            : $"{expanded}.jsonl";

        string? exact = Directory.EnumerateFiles(projectsRoot, targetName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (exact != null) return exact;

        // Prefix/partial UUID match fallback.
        string? partial = Directory.EnumerateFiles(projectsRoot, $"*{expanded}*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (partial != null) return partial;

        throw new FileNotFoundException($"Claude Code oturumu bulunamadı: {input}");
    }

    /// <summary>Lists recent or title-matching Claude Code sessions.</summary>
    public static List<SessionSummaryItem> ListSessions(int limit, string? query = null)
    {
        string projectsRoot = DefaultProjectsFolder();
        if (!Directory.Exists(projectsRoot))
            return [];

        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        int scanCap = hasQuery ? 500 : Math.Max(limit * 2, 40);

        IEnumerable<FileInfo> files = Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => !info.Name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(scanCap);

        List<SessionSummaryItem> items = [];
        foreach (FileInfo file in files)
        {
            (string id, string title, string cwd) = PeekSummary(file.FullName);
            if (hasQuery &&
                !title.Contains(query!, StringComparison.OrdinalIgnoreCase) &&
                !cwd.Contains(query!, StringComparison.OrdinalIgnoreCase) &&
                !id.Contains(query!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new SessionSummaryItem(
                id,
                title,
                new DateTimeOffset(file.LastWriteTime),
                cwd,
                file.FullName));

            if (items.Count >= limit) break;
        }

        return items;
    }

    static (string SessionId, string Title, string Cwd) PeekSummary(string path)
    {
        string sessionId = Path.GetFileNameWithoutExtension(path);
        string customTitle = string.Empty;
        string aiTitle = string.Empty;
        string cwd = string.Empty;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;

                // Fast pre-check to avoid parsing large tool output lines.
                if (cwd.Length == 0 && line.Contains("\"cwd\"", StringComparison.Ordinal))
                {
                    TryExtractField(line, "cwd", ref cwd);
                    TryExtractField(line, "sessionId", ref sessionId);
                }

                if (line.Contains("\"custom-title\"", StringComparison.Ordinal))
                {
                    TryExtractField(line, "customTitle", ref customTitle);
                }
                else if (line.Contains("\"ai-title\"", StringComparison.Ordinal))
                {
                    TryExtractField(line, "aiTitle", ref aiTitle);
                }
            }
        }
        catch { /* Skip unreadable lines during listing. */ }

        string title = customTitle.Length > 0 ? customTitle : (aiTitle.Length > 0 ? aiTitle : sessionId);
        return (sessionId, title, cwd);
    }

    static void TryExtractField(string jsonLine, string propertyName, ref string target)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty(propertyName, out JsonElement el) &&
                el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) target = val;
            }
        }
        catch { }
    }
}
