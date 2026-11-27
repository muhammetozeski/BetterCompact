using System.Text;
using System.Text.Json;

namespace BetterCompactCodex;

/// <summary>
/// Reads one Codex (ChatGPT) rollout .jsonl session into a normalized <see cref="CompactDocument"/>,
/// including thread title lookup, model/effort shares, billing token accounting, and tool breakdowns.
/// </summary>
sealed class TranscriptReader
{
    const bool IsLogEnabled = true;
    const string UserContentKindPrefix = "user.";

    static readonly string[] InjectedTextOpenings =
    [
        "<recommended_plugins>",
        "# AGENTS.md instructions for ",
        "<environment_context>",
        "<app-context>",
        "<skills_instructions>",
        "<permissions instructions>",
        "<collaboration_mode>",
        "<apps_instructions>",
        "<plugins_instructions>",
        "<multi_agent_mode>"
    ];

    readonly CommonOptions options;
    readonly List<CompactMessage> messages = [];
    readonly Dictionary<string, int> pendingToolCalls = [];
    readonly Dictionary<string, int> totalToolCounts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> modelCounts = new(StringComparer.OrdinalIgnoreCase);

    int totalLines;
    int unreadableLines;
    int userMessages;
    int assistantMessages;
    int reasoningItems;
    int injectedBlocks;
    int compactSummaries;
    int toolCalls;
    int toolResults;
    int attachments;
    int oversizedMessages;
    int pendingToolResults;

    string sessionId = string.Empty;
    string workingDirectory = string.Empty;
    DateTimeOffset? earliestTimestamp;
    DateTimeOffset? latestTimestamp;

    // Monotonic segment accumulator for event_msg/token_count so counter resets across compactions never undercount.
    int tokenEventsCount;
    long accInput;
    long accCachedInput;
    long accCacheWrite;
    long accOutput;
    long accReasoningOutput;
    long segInput;
    long segCachedInput;
    long segCacheWrite;
    long segOutput;
    long segReasoningOutput;
    long lastContextTokens;
    long? contextWindowLimit;

    public TranscriptReader(CommonOptions options) => this.options = options;

    /// <summary>Reads one Codex session file from start to end.</summary>
    public CompactDocument Read(string path)
    {
        Log($"Reading Codex session={path}", isRun: IsLogEnabled);

        foreach (string line in ReadLinesShared(path))
        {
            if (line.Length == 0) continue;
            totalLines++;

            try
            {
                using JsonDocument parsed = JsonDocument.Parse(line);
                ReadEntry(parsed.RootElement);
            }
            catch (JsonException)
            {
                unreadableLines++;
            }
        }

        FlushToolActivity();

        if (sessionId.Length == 0) sessionId = Path.GetFileNameWithoutExtension(path);

        Dictionary<string, (string Title, DateTimeOffset? UpdatedAt)> index = SessionLocator.LoadSessionIndex();
        string title = index.TryGetValue(sessionId, out var meta) && !string.IsNullOrWhiteSpace(meta.Title)
            ? meta.Title
            : $"Codex {sessionId}";

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
            HarnessDisplayName: "Codex",
            HarnessFileToken: "Codex",
            AssistantDisplayName: "Codex",
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
            DuplicateEntries: 0,
            UserMessages: userMessages,
            SlashCommands: 0,
            AssistantMessages: assistantMessages,
            ReasoningBlocks: reasoningItems,
            InjectedBlocks: injectedBlocks,
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
        string outerType = TextOf(entry, "type") ?? string.Empty;
        DateTimeOffset? timestamp = TimestampOf(entry);
        TrackTimestamp(timestamp);

        if (!entry.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        switch (outerType)
        {
            case "session_meta":
                sessionId = TextOf(payload, "session_id") ?? TextOf(payload, "id") ?? sessionId;
                workingDirectory = TextOf(payload, "cwd") ?? workingDirectory;
                long metaWindow = Int64Of(payload, "context_window");
                if (metaWindow > 0) contextWindowLimit = metaWindow;
                return;

            case "turn_context":
                string? model = TextOf(payload, "model");
                string? effort = TextOf(payload, "effort");
                if (!string.IsNullOrWhiteSpace(model))
                {
                    string display = ModelNaming.FormatDisplayName(model, effort);
                    modelCounts[display] = modelCounts.GetValueOrDefault(display) + 1;
                }
                if (workingDirectory.Length == 0)
                    workingDirectory = TextOf(payload, "cwd") ?? workingDirectory;
                return;

            case "event_msg":
                ReadEventMessage(payload);
                return;

            case "response_item":
                ReadResponseItem(payload, timestamp);
                return;

            case "compacted":
                ReadCompactedEntry(payload, timestamp);
                return;
        }
    }

    void ReadEventMessage(JsonElement payload)
    {
        string eventType = TextOf(payload, "type") ?? string.Empty;
        if (eventType != "token_count") return;
        if (!payload.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) return;

        tokenEventsCount++;
        long window = Int64Of(info, "model_context_window");
        if (window > 0) contextWindowLimit = window;

        if (info.TryGetProperty("last_token_usage", out JsonElement lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
        {
            long lastIn = Int64Of(lastUsage, "input_tokens");
            if (lastIn > 0) lastContextTokens = lastIn;
        }

        if (info.TryGetProperty("total_token_usage", out JsonElement totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
        {
            long inTok = Int64Of(totalUsage, "input_tokens");
            long cachedTok = Int64Of(totalUsage, "cached_input_tokens");
            long cacheWriteTok = Int64Of(totalUsage, "cache_write_input_tokens");
            long outTok = Int64Of(totalUsage, "output_tokens");
            long reasonTok = Int64Of(totalUsage, "reasoning_output_tokens");

            // If cumulative counter reset (e.g. session reload), lock previous segment before starting new one.
            if (inTok < segInput || outTok < segOutput)
            {
                accInput += segInput;
                accCachedInput += segCachedInput;
                accCacheWrite += segCacheWrite;
                accOutput += segOutput;
                accReasoningOutput += segReasoningOutput;
            }

            segInput = inTok;
            segCachedInput = cachedTok;
            segCacheWrite = cacheWriteTok;
            segOutput = outTok;
            segReasoningOutput = reasonTok;
        }
    }

    TokenUsageSummary? BuildTokenUsage()
    {
        if (tokenEventsCount == 0) return null;

        long totalInput = accInput + segInput;
        long totalCached = accCachedInput + segCachedInput;
        long totalCacheWrite = accCacheWrite + segCacheWrite;
        long totalOutput = accOutput + segOutput;
        long totalReasoning = accReasoningOutput + segReasoningOutput;
        long billedNewInput = Math.Max(0, totalInput - totalCached);

        return new TokenUsageSummary(
            LastContextTokens: lastContextTokens,
            ContextWindowLimit: contextWindowLimit,
            BilledNewInputTokens: billedNewInput,
            BilledOutputTokens: totalOutput,
            BilledReasoningOutputTokens: totalReasoning,
            CacheReadTokens: totalCached,
            CacheWriteTokens: totalCacheWrite,
            MeasuredApiCalls: tokenEventsCount);
    }

    void ReadResponseItem(JsonElement item, DateTimeOffset? timestamp)
    {
        string itemType = TextOf(item, "type") ?? string.Empty;
        switch (itemType)
        {
            case "message":
                ReadMessage(item, timestamp);
                return;

            case "reasoning":
                ReadReasoning(item, timestamp);
                return;

            case "compaction":
                compactSummaries++;
                return;
        }

        if (itemType.EndsWith("_call_output", StringComparison.Ordinal))
        {
            toolResults++;
            pendingToolResults++;
            return;
        }

        if (itemType.EndsWith("_call", StringComparison.Ordinal))
        {
            toolCalls++;
            string toolName = ToolName(item, itemType);
            pendingToolCalls[toolName] = pendingToolCalls.GetValueOrDefault(toolName) + 1;
            totalToolCounts[toolName] = totalToolCounts.GetValueOrDefault(toolName) + 1;
        }
    }

    void ReadMessage(JsonElement item, DateTimeOffset? timestamp)
    {
        string role = TextOf(item, "role") ?? string.Empty;
        string phase = TextOf(item, "phase") ?? string.Empty;

        if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return;

        string[] contentKinds = ContentItemKinds(item);
        int blockIndex = 0;

        foreach (JsonElement block in content.EnumerateArray())
        {
            string blockType = TextOf(block, "type") ?? string.Empty;
            string contentKind = blockIndex < contentKinds.Length ? contentKinds[blockIndex] : string.Empty;
            blockIndex++;

            if (blockType is "input_image" or "output_image")
            {
                attachments++;
                continue;
            }

            string? text = TextOf(block, "text");
            if (text == null) continue;

            if (role == "assistant")
            {
                assistantMessages++;
                CompactMessageKind kind = phase.Equals("commentary", StringComparison.OrdinalIgnoreCase)
                    ? CompactMessageKind.AssistantCommentary
                    : CompactMessageKind.Assistant;
                Add(kind, timestamp, text);
                continue;
            }

            bool isUserText = role == "user" && IsUserContent(contentKind, text);
            if (isUserText)
            {
                userMessages++;
                Add(CompactMessageKind.User, timestamp, text);
            }
            else
            {
                injectedBlocks++;
                if (options.KeepInjected) Add(CompactMessageKind.Injected, timestamp, text, role);
            }
        }
    }

    void ReadReasoning(JsonElement item, DateTimeOffset? timestamp)
    {
        reasoningItems++;
        if (!options.KeepReasoning) return;
        if (!item.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.Array) return;

        List<string> chunks = [];
        foreach (JsonElement block in summary.EnumerateArray())
        {
            string? text = TextOf(block, "text");
            if (!string.IsNullOrWhiteSpace(text)) chunks.Add(text.Trim());
        }

        if (chunks.Count > 0)
            Add(CompactMessageKind.Reasoning, timestamp, string.Join("\n\n", chunks));
    }

    void ReadCompactedEntry(JsonElement payload, DateTimeOffset? timestamp)
    {
        compactSummaries++;
        if (!options.KeepCompactSummaries) return;

        string? summary = TextOf(payload, "message");
        if (!string.IsNullOrWhiteSpace(summary)) Add(CompactMessageKind.CompactSummary, timestamp, summary);
    }

    void Add(CompactMessageKind kind, DateTimeOffset? timestamp, string rawText, string subtitle = "")
    {
        string text = rawText.Trim();
        if (text.Length == 0) return;
        FlushToolActivity();
        (string limited, bool wasTruncated) = ExportPipeline.ApplySizeLimit(text, options);
        if (wasTruncated) oversizedMessages++;
        messages.Add(new CompactMessage(kind, timestamp, limited, subtitle));
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

    void TrackTimestamp(DateTimeOffset? ts)
    {
        if (ts == null) return;
        if (earliestTimestamp == null || ts < earliestTimestamp) earliestTimestamp = ts;
        if (latestTimestamp == null || ts > latestTimestamp) latestTimestamp = ts;
    }

    static bool IsUserContent(string contentKind, string text)
    {
        if (contentKind.Length > 0)
            return contentKind.StartsWith(UserContentKindPrefix, StringComparison.OrdinalIgnoreCase);

        string trimmed = text.TrimStart();
        return !InjectedTextOpenings.Any(opening => trimmed.StartsWith(opening, StringComparison.OrdinalIgnoreCase));
    }

    static string[] ContentItemKinds(JsonElement item)
    {
        if (!item.TryGetProperty("internal_chat_message_metadata_passthrough", out JsonElement metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("content_item_kinds", out JsonElement kinds) ||
            kinds.ValueKind != JsonValueKind.Array)
            return [];

        return kinds.EnumerateArray()
            .Where(kind => kind.ValueKind == JsonValueKind.String)
            .Select(kind => kind.GetString() ?? string.Empty)
            .ToArray();
    }

    static string ToolName(JsonElement item, string itemType)
    {
        string name = TextOf(item, "name") ?? itemType[..^"_call".Length];
        string? ns = TextOf(item, "namespace");
        return string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
    }

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
}
