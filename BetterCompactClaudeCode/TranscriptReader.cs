using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterCompactClaudeCode;

/// <summary>
/// Reads one Claude Code .jsonl session into a normalized <see cref="CompactDocument"/>,
/// including deduplicated per-message billing token accounting, model shares, and tool stats.
/// </summary>
sealed partial class TranscriptReader
{
    const bool IsLogEnabled = true;
    const string HumanOrigin = "human";
    const string CompactHandoffOpening = "This session is being continued from a previous conversation";
    const string CommandMessageOpen = "<command-message>";
    const string CommandNameOpen = "<command-name>";
    const string CommandNameClose = "</command-name>";
    const string CommandArgumentsOpen = "<command-args>";
    const string CommandArgumentsClose = "</command-args>";
    const string QuoteReplyMarker = "<!-- attach -->";

    static readonly string[] InjectedOpeningTags =
    [
        "<task-notification>",
        "<local-command-caveat>",
        "<local-command-stdout>",
        "<user-memory-input>",
        "<ide_opened_file>",
        "<ci-monitor-event>"
    ];

    readonly CommonOptions options;
    readonly HashSet<string> seenEntryIds = [];
    readonly List<CompactMessage> messages = [];
    readonly Dictionary<string, int> pendingToolCalls = [];
    readonly Dictionary<string, int> totalToolCounts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> modelCounts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MessageUsageRecord> usageByMessageId = new(StringComparer.Ordinal);

    int totalLines;
    int unreadableLines;
    int duplicateEntries;
    int userMessages;
    int slashCommands;
    int assistantMessages;
    int reasoningBlocks;
    int injectedMessages;
    int compactSummaries;
    int toolCalls;
    int toolResults;
    int attachments;
    int oversizedMessages;
    int pendingToolResults;
    int syntheticUsageCounter;

    string customTitle = string.Empty;
    string aiTitle = string.Empty;
    string sessionId = string.Empty;
    string workingDirectory = string.Empty;
    DateTimeOffset? earliestTimestamp;
    DateTimeOffset? latestTimestamp;
    long lastContextTokens;

    public TranscriptReader(CommonOptions options) => this.options = options;

    /// <summary>Reads one Claude Code session file from start to end.</summary>
    public CompactDocument Read(string path)
    {
        Log($"Reading Claude Code session={path}", isRun: IsLogEnabled);

        foreach (string line in ReadLinesShared(path))
        {
            if (line.Length == 0) continue;
            totalLines++;

            JsonDocument? parsed = null;
            try { parsed = JsonDocument.Parse(line); }
            catch (JsonException) { unreadableLines++; }

            if (parsed == null) continue;
            using (parsed) ReadEntry(parsed.RootElement);
        }

        FlushToolActivity();

        string title = customTitle.Length > 0
            ? customTitle
            : (aiTitle.Length > 0 ? aiTitle : Path.GetFileNameWithoutExtension(path));
        if (sessionId.Length == 0) sessionId = Path.GetFileNameWithoutExtension(path);

        (string primaryModel, string modelFamily, IReadOnlyList<ModelUsageShare> modelShares) =
            ModelNaming.SummarizeModels(modelCounts);

        TokenUsageSummary? tokenUsage = BuildTokenUsage();

        List<KeyValuePair<string, int>> topTools = totalToolCounts
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CompactMetadata metadata = new(
            SessionId: sessionId,
            Title: title,
            HarnessDisplayName: "Claude Code",
            HarnessFileToken: "ClaudeCode",
            AssistantDisplayName: "Claude",
            PrimaryModelDisplay: primaryModel,
            ModelFamily: modelFamily,
            ModelShares: modelShares,
            SourcePaths: [Path.GetFullPath(path)],
            WorkingDirectory: workingDirectory,
            ParentSessionId: string.Empty,
            StartedAt: earliestTimestamp,
            EndedAt: latestTimestamp,
            ExportedAt: DateTimeOffset.Now,
            TokenUsage: tokenUsage,
            SourceSizeBytes: new FileInfo(path).Length);

        CompactStatistics statistics = new(
            TotalEntries: totalLines,
            UnreadableEntries: unreadableLines,
            DuplicateEntries: duplicateEntries,
            UserMessages: userMessages,
            SlashCommands: slashCommands,
            AssistantMessages: assistantMessages,
            ReasoningBlocks: reasoningBlocks,
            InjectedBlocks: injectedMessages,
            CompactSummaries: compactSummaries,
            ToolCalls: toolCalls,
            ToolResults: toolResults,
            Attachments: attachments,
            OversizedMessages: oversizedMessages,
            TopTools: topTools,
            EntryUnitName: "kayıt");

        return new CompactDocument(metadata, messages, statistics);
    }

    static IEnumerable<string> ReadLinesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line) yield return line;
    }

    void ReadEntry(JsonElement entry)
    {
        if (sessionId.Length == 0) sessionId = TextOf(entry, "sessionId") ?? string.Empty;
        if (workingDirectory.Length == 0) workingDirectory = TextOf(entry, "cwd") ?? string.Empty;

        DateTimeOffset? ts = TimestampOf(entry);
        TrackTimestamp(ts);

        switch (TextOf(entry, "type"))
        {
            case "custom-title":
                customTitle = TextOf(entry, "customTitle") ?? customTitle;
                return;

            case "ai-title":
                aiTitle = TextOf(entry, "aiTitle") ?? aiTitle;
                return;

            case "attachment":
                attachments++;
                return;

            case "user":
                ReadUserEntry(entry, ts);
                return;

            case "assistant":
                ReadAssistantEntry(entry, ts);
                return;
        }
    }

    void ReadUserEntry(JsonElement entry, DateTimeOffset? timestamp)
    {
        if (IsRepeat(entry)) return;

        JsonElement? content = ContentOf(entry);
        if (content == null) return;

        if (content.Value.ValueKind == JsonValueKind.String)
        {
            AddUserText(entry, content.Value.GetString() ?? string.Empty, timestamp);
            return;
        }

        if (content.Value.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement block in content.Value.EnumerateArray())
        {
            switch (TextOf(block, "type"))
            {
                case "text":
                    AddUserText(entry, TextOf(block, "text") ?? string.Empty, timestamp);
                    break;

                case "tool_result":
                    toolResults++;
                    pendingToolResults++;
                    break;
            }
        }
    }

    void ReadAssistantEntry(JsonElement entry, DateTimeOffset? timestamp)
    {
        if (IsRepeat(entry)) return;

        if (entry.TryGetProperty("message", out JsonElement msgObj) && msgObj.ValueKind == JsonValueKind.Object)
        {
            string? rawModel = TextOf(msgObj, "model");
            string? effort = TextOf(entry, "effort");
            string messageId = TextOf(msgObj, "id") ?? $"anon_{++syntheticUsageCounter}";

            bool isNewApiMessage = !usageByMessageId.ContainsKey(messageId);
            if (!string.IsNullOrWhiteSpace(rawModel) && isNewApiMessage)
            {
                string display = ModelNaming.FormatDisplayName(rawModel, effort);
                modelCounts[display] = modelCounts.GetValueOrDefault(display) + 1;
            }

            if (msgObj.TryGetProperty("usage", out JsonElement usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                long inputTokens = Int64Of(usageEl, "input_tokens");
                long cacheWrite = Int64Of(usageEl, "cache_creation_input_tokens");
                long cacheRead = Int64Of(usageEl, "cache_read_input_tokens");
                long outputTokens = Int64Of(usageEl, "output_tokens");

                MessageUsageRecord candidate = new(inputTokens, cacheWrite, cacheRead, outputTokens);
                if (!usageByMessageId.TryGetValue(messageId, out MessageUsageRecord existing) ||
                    candidate.OutputTokens >= existing.OutputTokens)
                {
                    usageByMessageId[messageId] = candidate;
                }

                long ctx = inputTokens + cacheWrite + cacheRead;
                if (ctx > 0) lastContextTokens = ctx;
            }
        }

        JsonElement? content = ContentOf(entry);
        if (content == null || content.Value.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement block in content.Value.EnumerateArray())
        {
            switch (TextOf(block, "type"))
            {
                case "text":
                    string reply = (TextOf(block, "text") ?? string.Empty).Trim();
                    if (reply.Length == 0) break;
                    assistantMessages++;
                    Add(CompactMessageKind.Assistant, timestamp, reply);
                    break;

                case "thinking":
                    string reasoning = (TextOf(block, "thinking") ?? string.Empty).Trim();
                    if (reasoning.Length == 0) break;
                    reasoningBlocks++;
                    if (options.KeepReasoning) Add(CompactMessageKind.Reasoning, timestamp, reasoning);
                    break;

                case "tool_use":
                    toolCalls++;
                    string toolName = TextOf(block, "name") ?? "unknown";
                    pendingToolCalls[toolName] = pendingToolCalls.GetValueOrDefault(toolName) + 1;
                    totalToolCounts[toolName] = totalToolCounts.GetValueOrDefault(toolName) + 1;
                    break;
            }
        }
    }

    TokenUsageSummary? BuildTokenUsage()
    {
        if (usageByMessageId.Count == 0) return null;

        long newInput = 0, cacheWrite = 0, cacheRead = 0, output = 0;
        foreach (MessageUsageRecord rec in usageByMessageId.Values)
        {
            newInput += rec.InputTokens;
            cacheWrite += rec.CacheWriteTokens;
            cacheRead += rec.CacheReadTokens;
            output += rec.OutputTokens;
        }

        return new TokenUsageSummary(
            LastContextTokens: lastContextTokens,
            ContextWindowLimit: null,
            BilledNewInputTokens: newInput,
            BilledOutputTokens: output,
            BilledReasoningOutputTokens: 0,
            CacheReadTokens: cacheRead,
            CacheWriteTokens: cacheWrite,
            MeasuredApiCalls: usageByMessageId.Count);
    }

    void AddUserText(JsonElement entry, string rawText, DateTimeOffset? timestamp)
    {
        string text = rawText.Trim();
        if (text.Length == 0) return;

        switch (KindOf(entry, text))
        {
            case CompactMessageKind.SlashCommand:
                slashCommands++;
                Add(CompactMessageKind.SlashCommand, timestamp, FlattenSlashCommand(text));
                break;

            case CompactMessageKind.Injected:
                injectedMessages++;
                if (options.KeepInjected) Add(CompactMessageKind.Injected, timestamp, text);
                break;

            case CompactMessageKind.CompactSummary:
                compactSummaries++;
                if (options.KeepCompactSummaries) Add(CompactMessageKind.CompactSummary, timestamp, text);
                break;

            default:
                userMessages++;
                Add(CompactMessageKind.User, timestamp, Clean(text));
                break;
        }
    }

    static CompactMessageKind KindOf(JsonElement entry, string text)
    {
        bool isCommandEntry = IsCommandEntry(text);

        if (entry.TryGetProperty("origin", out JsonElement origin) && origin.ValueKind == JsonValueKind.Object)
            return TextOf(origin, "kind") == HumanOrigin
                ? isCommandEntry ? CompactMessageKind.SlashCommand : CompactMessageKind.User
                : CompactMessageKind.Injected;

        if (entry.TryGetProperty("isMeta", out JsonElement isMeta) && isMeta.ValueKind == JsonValueKind.True)
            return CompactMessageKind.Injected;

        if (entry.TryGetProperty("isCompactSummary", out JsonElement isSummary) && isSummary.ValueKind == JsonValueKind.True)
            return CompactMessageKind.CompactSummary;

        if (text.StartsWith(CompactHandoffOpening, StringComparison.Ordinal))
            return CompactMessageKind.CompactSummary;

        if (isCommandEntry) return CompactMessageKind.SlashCommand;

        foreach (string tag in InjectedOpeningTags)
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                return CompactMessageKind.Injected;

        return CompactMessageKind.User;
    }

    static bool IsCommandEntry(string text) =>
        text.StartsWith(CommandMessageOpen, StringComparison.Ordinal) ||
        text.StartsWith(CommandNameOpen, StringComparison.Ordinal);

    static string FlattenSlashCommand(string text)
    {
        string? name = Between(text, CommandNameOpen, CommandNameClose);
        if (name == null) return text;
        string? args = Between(text, CommandArgumentsOpen, CommandArgumentsClose);
        return args is { Length: > 0 } ? $"{name.Trim()}\n{args.Trim()}" : name.Trim();
    }

    string Clean(string text)
    {
        if (!options.KeepSystemReminders) text = SystemReminderBlock().Replace(text, string.Empty);
        return text.Replace(QuoteReplyMarker, string.Empty).Trim();
    }

    void Add(CompactMessageKind kind, DateTimeOffset? timestamp, string text)
    {
        if (text.Length == 0) return;
        FlushToolActivity();
        (string limited, bool wasTruncated) = ExportPipeline.ApplySizeLimit(text, options);
        if (wasTruncated) oversizedMessages++;
        messages.Add(new CompactMessage(kind, timestamp, limited));
    }

    void FlushToolActivity()
    {
        if (pendingToolCalls.Count == 0 && pendingToolResults == 0) return;

        if (options.KeepToolActivity)
        {
            int callCount = pendingToolCalls.Values.Sum();
            string breakdown = string.Join(", ", pendingToolCalls
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key} x{p.Value}"));

            string summary = breakdown.Length > 0
                ? $"{callCount} araç çağrısı — {breakdown}"
                : $"{pendingToolResults} araç sonucu";
            messages.Add(new CompactMessage(CompactMessageKind.ToolActivity, null, summary));
        }

        pendingToolCalls.Clear();
        pendingToolResults = 0;
    }

    bool IsRepeat(JsonElement entry)
    {
        string? entryId = TextOf(entry, "uuid");
        if (entryId == null) return false;
        if (seenEntryIds.Add(entryId)) return false;
        duplicateEntries++;
        return true;
    }

    void TrackTimestamp(DateTimeOffset? ts)
    {
        if (ts == null) return;
        if (earliestTimestamp == null || ts < earliestTimestamp) earliestTimestamp = ts;
        if (latestTimestamp == null || ts > latestTimestamp) latestTimestamp = ts;
    }

    static JsonElement? ContentOf(JsonElement entry) =>
        entry.TryGetProperty("message", out JsonElement msg) && msg.TryGetProperty("content", out JsonElement content)
            ? content
            : null;

    static DateTimeOffset? TimestampOf(JsonElement entry) =>
        DateTimeOffset.TryParse(TextOf(entry, "timestamp"), out DateTimeOffset parsed) ? parsed.ToLocalTime() : null;

    static string? TextOf(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static long Int64Of(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)
            ? n
            : 0;

    static string? Between(string text, string opening, string closing)
    {
        int start = text.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0) return null;
        start += opening.Length;
        int end = text.IndexOf(closing, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    readonly record struct MessageUsageRecord(long InputTokens, long CacheWriteTokens, long CacheReadTokens, long OutputTokens);

    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminderBlock();
}
