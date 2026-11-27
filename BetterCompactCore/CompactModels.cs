using System.Globalization;

namespace BetterCompactCore;

/// <summary>Identifies the kind of message stored in a normalized transcript.</summary>
public enum CompactMessageKind
{
    User,
    SlashCommand,
    Assistant,
    AssistantCommentary,
    Reasoning,
    Injected,
    CompactSummary,
    ToolActivity
}

/// <summary>Represents a single normalized message or tool activity marker.</summary>
public sealed record CompactMessage(
    CompactMessageKind Kind,
    DateTimeOffset? Timestamp,
    string Text,
    string Subtitle = "");

/// <summary>Represents the share of a model used during a conversation.</summary>
public sealed record ModelUsageShare(string DisplayName, int Count, double Percentage);

/// <summary>
/// Holds both the current context window usage and the per-request cumulative billing token totals.
/// </summary>
public sealed record TokenUsageSummary(
    long LastContextTokens,
    long? ContextWindowLimit,
    long BilledNewInputTokens,
    long BilledOutputTokens,
    long BilledReasoningOutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int MeasuredApiCalls)
{
    /// <summary>Total non-cached tokens spent across all API calls (new input + cache writes + outputs).</summary>
    public long BilledTotalTokens => BilledNewInputTokens + CacheWriteTokens + BilledOutputTokens;

    /// <summary>Total tokens processed across all API calls including cache reads.</summary>
    public long GrossTotalTokens => BilledTotalTokens + CacheReadTokens;

    /// <summary>Percentage of all input tokens satisfied from prompt cache.</summary>
    public double CacheHitPercent
    {
        get
        {
            long totalInput = BilledNewInputTokens + CacheWriteTokens + CacheReadTokens;
            return totalInput <= 0 ? 0 : (CacheReadTokens * 100.0) / totalInput;
        }
    }

    /// <summary>Returns true when at least one non-zero token metric was extracted.</summary>
    public bool HasData => MeasuredApiCalls > 0 || LastContextTokens > 0 || GrossTotalTokens > 0;
}

/// <summary>Counts kept and excluded items across a conversation.</summary>
public sealed record CompactStatistics(
    int TotalEntries,
    int UnreadableEntries,
    int DuplicateEntries,
    int UserMessages,
    int SlashCommands,
    int AssistantMessages,
    int ReasoningBlocks,
    int InjectedBlocks,
    int CompactSummaries,
    int ToolCalls,
    int ToolResults,
    int Attachments,
    int OversizedMessages,
    IReadOnlyList<KeyValuePair<string, int>> TopTools,
    string EntryUnitName = "kayıt");

/// <summary>Normalized metadata shared across all BetterCompact harnesses.</summary>
public sealed record CompactMetadata(
    string SessionId,
    string Title,
    string HarnessDisplayName,
    string HarnessFileToken,
    string AssistantDisplayName,
    string PrimaryModelDisplay,
    string ModelFamily,
    IReadOnlyList<ModelUsageShare> ModelShares,
    IReadOnlyList<string> SourcePaths,
    string WorkingDirectory,
    string ParentSessionId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset ExportedAt,
    TokenUsageSummary? TokenUsage,
    long SourceSizeBytes);

/// <summary>Represents a fully extracted conversation ready for Markdown composition.</summary>
public sealed record CompactDocument(
    CompactMetadata Metadata,
    IReadOnlyList<CompactMessage> Messages,
    CompactStatistics Statistics);

/// <summary>Describes one conversation discovered during --list or --find.</summary>
public sealed record SessionSummaryItem(
    string SessionId,
    string Title,
    DateTimeOffset? LastActivity,
    string WorkingDirectory,
    string SourcePath);

/// <summary>Provides shared model display-name normalization and model-family detection.</summary>
public static class ModelNaming
{
    /// <summary>Derives a human-readable model family name from a raw or formatted model name.</summary>
    public static string InferFamily(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return "Bilinmiyor";
        string lower = modelName.ToLowerInvariant();
        if (lower.Contains("gemini") || lower.Contains("neon") || lower.Contains("hydrogen")) return "Gemini";
        if (lower.Contains("claude") || lower.Contains("opus") || lower.Contains("sonnet") || lower.Contains("haiku") || lower.Contains("fable")) return "Claude";
        if (lower.Contains("gpt") || lower.Contains("codex") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4")) return "GPT";
        if (lower.Contains("deepseek")) return "DeepSeek";
        if (lower.Contains("llama")) return "Llama";
        if (lower.Contains("qwen")) return "Qwen";

        string firstToken = modelName.Split([' ', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Bilinmiyor";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(firstToken.ToLowerInvariant());
    }

    /// <summary>
    /// Formats a model identifier and optional effort level into a clean display label
    /// such as "Gemini 4 Neon High" or "GPT-6 Luna Medium".
    /// </summary>
    public static string FormatDisplayName(string rawModel, string? effort = null)
    {
        if (string.IsNullOrWhiteSpace(rawModel)) return "Bilinmiyor";

        string clean = rawModel.Trim();
        // If already human readable (contains spaces, e.g. "Gemini 4 Neon (High)"), remove parentheses cleanly.
        if (clean.Contains(' '))
        {
            clean = clean.Replace("(", string.Empty).Replace(")", string.Empty);
            while (clean.Contains("  ", StringComparison.Ordinal))
                clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        }
        else
        {
            // Convert slugs like "gpt-6-luna" or "claude-fable-5" or "gemini-4-neon-high" to readable Title Case.
            string[] parts = clean.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
            List<string> formatted = [];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Equals("gpt", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < parts.Length && char.IsDigit(parts[i + 1][0]))
                    {
                        formatted.Add($"GPT-{parts[i + 1]}");
                        i++;
                    }
                    else
                    {
                        formatted.Add("GPT");
                    }
                }
                else if (p.Equals("ai", StringComparison.OrdinalIgnoreCase) || p.Equals("cli", StringComparison.OrdinalIgnoreCase))
                {
                    formatted.Add(p.ToUpperInvariant());
                }
                else if (p.Length > 0 && char.IsLetter(p[0]))
                {
                    formatted.Add(char.ToUpperInvariant(p[0]) + p[1..]);
                }
                else
                {
                    formatted.Add(p);
                }
            }
            clean = string.Join(' ', formatted);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            string normalizedEffort = FormatEffort(effort);
            if (normalizedEffort.Length > 0 &&
                !clean.Contains(normalizedEffort, StringComparison.OrdinalIgnoreCase))
            {
                clean = $"{clean} {normalizedEffort}";
            }
        }

        return clean.Trim();
    }

    static string FormatEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "xhigh" or "extra_high" or "extra-high" => "Extra High",
        "high" => "High",
        "medium" or "med" => "Medium",
        "low" => "Low",
        "minimal" or "min" => "Minimal",
        "none" or "default" => string.Empty,
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(effort.Trim().ToLowerInvariant())
    };

    /// <summary>Builds sorted usage share statistics from per-turn model counts.</summary>
    public static (string PrimaryModel, string Family, IReadOnlyList<ModelUsageShare> Shares) SummarizeModels(
        Dictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return ("Bilinmiyor", "Bilinmiyor", []);

        int total = counts.Values.Sum();
        List<ModelUsageShare> shares = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ModelUsageShare(
                pair.Key,
                pair.Value,
                total > 0 ? (pair.Value * 100.0) / total : 0))
            .ToList();

        string primary = shares[0].DisplayName;
        return (primary, InferFamily(primary), shares);
    }
}
