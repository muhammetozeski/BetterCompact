using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BetterCompactAntigravity;

/// <summary>
/// Reads Antigravity's conversation_summaries.db and per-conversation SQLite databases read-only,
/// extracting messages, reasoning blocks, tool breakdowns, timestamps, models, and billing tokens.
/// </summary>
sealed class ConversationReader
{
    const bool IsLogEnabled = true;
    const int ToolCallStepType = 132;
    const int ActivityStepType = 101;
    const int GenerationStepType = 15;
    const int SummaryStepType = 23;

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    readonly CommonOptions options;

    public ConversationReader(CommonOptions options) => this.options = options;

    /// <summary>Returns the default Antigravity data folder (%USERPROFILE%\.gemini\antigravity).</summary>
    public static string DefaultAntigravityFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");

    /// <summary>Lists recent or title-matching Antigravity conversations from conversation_summaries.db.</summary>
    public static List<SessionSummaryItem> ListSessions(int limit, string? query = null)
    {
        string antigravityFolder = DefaultAntigravityFolder();
        string summariesPath = Path.Combine(antigravityFolder, "conversation_summaries.db");
        if (!File.Exists(summariesPath))
            throw new FileNotFoundException($"Antigravity özet veritabanı bulunamadı: {summariesPath}");

        using NativeSqlite db = NativeSqlite.OpenReadOnly(summariesPath);
        string sql = string.IsNullOrWhiteSpace(query)
            ? $"SELECT conversation_id, title, last_modified_time, workspace_uris FROM conversation_summaries ORDER BY last_modified_time DESC LIMIT {Math.Max(1, limit)};"
            : $"SELECT conversation_id, title, last_modified_time, workspace_uris FROM conversation_summaries WHERE title LIKE ? OR workspace_uris LIKE ? ORDER BY last_modified_time DESC LIMIT {Math.Max(1, limit)};";

        using NativeSqlite.SqliteStatement stmt = string.IsNullOrWhiteSpace(query)
            ? db.Prepare(sql)
            : db.Prepare(sql, $"%{query}%", $"%{query}%");

        List<SessionSummaryItem> items = [];
        while (stmt.Read())
        {
            string id = stmt.GetText(0);
            string title = stmt.GetText(1);
            DateTimeOffset? lastMod = ParseIsoTimestamp(stmt.GetText(2));
            string workspace = ParseFirstWorkspacePath(stmt.GetText(3));
            string dbPath = Path.Combine(antigravityFolder, "conversations", id + ".db");
            items.Add(new SessionSummaryItem(id, title, lastMod, workspace, dbPath));
        }

        return items;
    }

    /// <summary>Reads one Antigravity conversation into a normalized <see cref="CompactDocument"/>.</summary>
    public CompactDocument Read(string conversationId, string? conversationPath = null)
    {
        string antigravityFolder = DefaultAntigravityFolder();
        conversationPath ??= Path.Combine(antigravityFolder, "conversations", conversationId + ".db");
        string summariesPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(conversationPath)!)!, "conversation_summaries.db");

        if (!File.Exists(conversationPath))
            throw new FileNotFoundException($"Antigravity sohbet veritabanı bulunamadı: {conversationPath}");
        if (!File.Exists(summariesPath))
            throw new FileNotFoundException($"Antigravity özet veritabanı bulunamadı: {summariesPath}");

        Log($"Reading Antigravity conversation={conversationId}; db={conversationPath}", isRun: IsLogEnabled);

        SummaryRow summary = ReadSummary(summariesPath, conversationId);
        List<IndexedMessage> baseMessages = ReadFtsMessages(summariesPath, conversationId);

        StepScanResult scan = ScanConversationDatabase(conversationPath);

        if (baseMessages.Count == 0)
            throw new InvalidDataException("Bu Antigravity sohbetinde dışa aktarılacak mesaj bulunamadı.");

        int oversizedCount = 0;
        List<CompactMessage> finalMessages = MergeTimeline(baseMessages, scan, ref oversizedCount);

        (string primaryModel, string modelFamily, IReadOnlyList<ModelUsageShare> modelShares) =
            ModelNaming.SummarizeModels(scan.ModelCounts);

        List<KeyValuePair<string, int>> topTools = scan.ToolNameCounts
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        TokenUsageSummary? tokenUsage = scan.MeasuredApiCalls > 0
            ? new TokenUsageSummary(
                scan.LastContextTokens,
                scan.ContextWindowLimit,
                scan.TotalNewInput,
                scan.TotalOutput,
                scan.TotalReasoningOutput,
                scan.TotalCacheHit,
                CacheWriteTokens: 0,
                scan.MeasuredApiCalls)
            : null;

        DateTimeOffset? endedAt = scan.LatestTimestamp ?? summary.LastModified ?? summary.LastUserInput;
        long sourceSize = new FileInfo(conversationPath).Length;

        CompactMetadata metadata = new(
            SessionId: conversationId,
            Title: summary.Title,
            HarnessDisplayName: "Antigravity",
            HarnessFileToken: "Antigravity",
            AssistantDisplayName: "Antigravity",
            PrimaryModelDisplay: primaryModel,
            ModelFamily: modelFamily,
            ModelShares: modelShares,
            SourcePaths: [summariesPath, conversationPath],
            WorkingDirectory: summary.WorkspacePath,
            ParentSessionId: summary.ParentConversationId,
            StartedAt: scan.EarliestTimestamp,
            EndedAt: endedAt,
            ExportedAt: DateTimeOffset.Now,
            TokenUsage: tokenUsage,
            SourceSizeBytes: sourceSize);

        CompactStatistics statistics = new(
            TotalEntries: Math.Max(summary.StepCount, scan.TotalSteps),
            UnreadableEntries: 0,
            DuplicateEntries: 0,
            UserMessages: baseMessages.Count(m => m.Kind == CompactMessageKind.User),
            SlashCommands: 0,
            AssistantMessages: finalMessages.Count(m => m.Kind == CompactMessageKind.Assistant),
            ReasoningBlocks: scan.ReasoningEntries.Count,
            InjectedBlocks: 0,
            CompactSummaries: scan.SummarySteps,
            ToolCalls: scan.ToolCallSteps,
            ToolResults: 0,
            Attachments: 0,
            OversizedMessages: oversizedCount,
            TopTools: topTools,
            EntryUnitName: "adım");

        return new CompactDocument(metadata, finalMessages, statistics);
    }

    List<CompactMessage> MergeTimeline(List<IndexedMessage> ftsMessages, StepScanResult scan, ref int oversizedCount)
    {
        // Build an ordered timeline combining FTS user/assistant messages, reasoning blocks, and tool calls.
        List<(int StepIndex, int OrderPriority, CompactMessage Message)> timeline = [];

        foreach (IndexedMessage msg in ftsMessages)
        {
            scan.StepTimestampsByIdx.TryGetValue(msg.StepIndex, out DateTimeOffset? ts);
            (string trimmed, bool wasTruncated) = ExportPipeline.ApplySizeLimit(msg.Text.Trim(), options);
            if (wasTruncated) oversizedCount++;
            if (trimmed.Length == 0) continue;

            int priority = msg.Kind == CompactMessageKind.User ? 10 : 30;
            timeline.Add((msg.StepIndex, priority, new CompactMessage(msg.Kind, ts, trimmed)));
        }

        if (options.KeepReasoning)
        {
            foreach (ReasoningEntry r in scan.ReasoningEntries)
            {
                (string trimmed, bool wasTruncated) = ExportPipeline.ApplySizeLimit(r.Text, options);
                if (wasTruncated) oversizedCount++;
                if (trimmed.Length == 0) continue;
                // Place reasoning right before assistant text of the same step.
                timeline.Add((r.StepIndex, 20, new CompactMessage(CompactMessageKind.Reasoning, r.Timestamp, trimmed)));
            }
        }

        timeline.Sort((a, b) =>
        {
            int cmp = a.StepIndex.CompareTo(b.StepIndex);
            return cmp != 0 ? cmp : a.OrderPriority.CompareTo(b.OrderPriority);
        });

        // Interleave grouped tool activity summaries between messages while merging consecutive assistant chunks.
        List<CompactMessage> result = [];
        int toolCursor = 0;
        Dictionary<string, int> pendingTools = [];

        void FlushTools()
        {
            if (pendingTools.Count == 0) return;
            if (options.KeepToolActivity)
            {
                int total = pendingTools.Values.Sum();
                string breakdown = string.Join(", ", pendingTools
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"{p.Key} x{p.Value}"));
                result.Add(new CompactMessage(CompactMessageKind.ToolActivity, null, $"{total} araç çağrısı — {breakdown}"));
            }
            pendingTools.Clear();
        }

        foreach (var entry in timeline)
        {
            while (toolCursor < scan.ToolEvents.Count && scan.ToolEvents[toolCursor].StepIndex < entry.StepIndex)
            {
                string toolName = scan.ToolEvents[toolCursor].ToolName;
                pendingTools[toolName] = pendingTools.GetValueOrDefault(toolName) + 1;
                toolCursor++;
            }

            FlushTools();

            // Merge back-to-back assistant reply fragments if no tool call or reasoning separated them.
            if (entry.Message.Kind == CompactMessageKind.Assistant &&
                result.Count > 0 &&
                result[^1].Kind == CompactMessageKind.Assistant)
            {
                CompactMessage prev = result[^1];
                result[^1] = prev with { Text = $"{prev.Text}\n\n{entry.Message.Text}" };
            }
            else
            {
                result.Add(entry.Message);
            }
        }

        while (toolCursor < scan.ToolEvents.Count)
        {
            string toolName = scan.ToolEvents[toolCursor].ToolName;
            pendingTools[toolName] = pendingTools.GetValueOrDefault(toolName) + 1;
            toolCursor++;
        }
        FlushTools();

        return result;
    }

    StepScanResult ScanConversationDatabase(string path)
    {
        StepScanResult scan = new();
        using NativeSqlite db = NativeSqlite.OpenReadOnly(path);

        using (NativeSqlite.SqliteStatement stmt = db.Prepare(
            "SELECT idx, step_type, metadata, CASE WHEN step_type = 15 THEN step_payload ELSE NULL END FROM steps ORDER BY idx ASC;"))
        {
            while (stmt.Read())
            {
                scan.TotalSteps++;
                int idx = checked((int)stmt.GetInt64(0));
                int stepType = checked((int)stmt.GetInt64(1));
                byte[] metadata = stmt.GetBlob(2);

                ProtobufReader.StepTimestamps ts = metadata.Length > 0
                    ? ProtobufReader.ExtractTimestamps(metadata)
                    : default;

                if (ts.CreatedAt != null)
                {
                    scan.StepTimestampsByIdx[idx] = ts.CreatedAt;
                    if (scan.EarliestTimestamp == null || ts.CreatedAt < scan.EarliestTimestamp)
                        scan.EarliestTimestamp = ts.CreatedAt;
                }
                if (ts.CompletedAt != null)
                {
                    if (scan.LatestTimestamp == null || ts.CompletedAt > scan.LatestTimestamp)
                        scan.LatestTimestamp = ts.CompletedAt;
                }

                switch (stepType)
                {
                    case GenerationStepType:
                        scan.GenerationSteps++;
                        if (metadata.Length > 0)
                        {
                            ProtobufReader.StepUsage usage = ProtobufReader.ExtractStepUsage(metadata);
                            if (usage.HasUsage)
                            {
                                scan.MeasuredApiCalls++;
                                scan.TotalNewInput += usage.NewInput;
                                scan.TotalOutput += usage.OutputTotal;
                                scan.TotalCacheHit += usage.CacheHit;
                                scan.TotalReasoningOutput += usage.OutputThinking;
                                scan.LastContextTokens = usage.NewInput + usage.CacheHit;
                            }
                        }

                        byte[] payload = stmt.GetBlob(3);
                        if (payload.Length > 0)
                        {
                            string? reasoning = ProtobufReader.ExtractReasoningText(payload);
                            if (!string.IsNullOrWhiteSpace(reasoning))
                                scan.ReasoningEntries.Add(new ReasoningEntry(idx, ts.CreatedAt, reasoning));
                        }
                        break;

                    case ToolCallStepType:
                        scan.ToolCallSteps++;
                        string toolName = metadata.Length > 0
                            ? (ProtobufReader.ExtractToolName(metadata) ?? "tool")
                            : "tool";
                        scan.ToolNameCounts[toolName] = scan.ToolNameCounts.GetValueOrDefault(toolName) + 1;
                        scan.ToolEvents.Add(new ToolEvent(idx, toolName));
                        break;

                    case ActivityStepType:
                        scan.ActivitySteps++;
                        break;

                    case SummaryStepType:
                        scan.SummarySteps++;
                        break;
                }
            }
        }

        // Extract model names and context window limit from gen_metadata.
        try
        {
            using NativeSqlite.SqliteStatement genStmt = db.Prepare("SELECT data FROM gen_metadata ORDER BY idx ASC;");
            while (genStmt.Read())
            {
                byte[] blob = genStmt.GetBlob(0);
                if (blob.Length == 0) continue;

                ProtobufReader.GenMetadataInfo info = ProtobufReader.ExtractGenMetadata(blob);
                if (info.ContextLimit is > 0)
                    scan.ContextWindowLimit = info.ContextLimit;

                string? rawModel = !string.IsNullOrWhiteSpace(info.ModelDisplay) ? info.ModelDisplay : info.ModelSlug;
                if (!string.IsNullOrWhiteSpace(rawModel))
                {
                    string formatted = ModelNaming.FormatDisplayName(rawModel);
                    scan.ModelCounts[formatted] = scan.ModelCounts.GetValueOrDefault(formatted) + 1;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Optional gen_metadata read failed: {ex.Message}", isRun: IsLogEnabled);
        }

        return scan;
    }

    static SummaryRow ReadSummary(string path, string conversationId)
    {
        using NativeSqlite db = NativeSqlite.OpenReadOnly(path);
        using NativeSqlite.SqliteStatement stmt = db.Prepare(
            "SELECT title, last_user_input_time, last_modified_time, step_count, workspace_uris, parent_conversation_id FROM conversation_summaries WHERE conversation_id = ?;",
            conversationId);

        if (!stmt.Read())
            throw new FileNotFoundException($"Antigravity özet veritabanında sohbet bulunamadı: {conversationId}");

        string title = stmt.GetText(0);
        DateTimeOffset? lastUser = ParseIsoTimestamp(stmt.GetText(1));
        DateTimeOffset? lastModified = ParseIsoTimestamp(stmt.GetText(2));
        int stepCount = checked((int)stmt.GetInt64(3));
        string workspace = ParseFirstWorkspacePath(stmt.GetText(4));
        string parentId = stmt.GetText(5).Trim();

        return new SummaryRow(
            string.IsNullOrWhiteSpace(title) ? conversationId : title,
            lastUser,
            lastModified,
            stepCount,
            workspace,
            parentId);
    }

    static List<IndexedMessage> ReadFtsMessages(string path, string conversationId)
    {
        using NativeSqlite db = NativeSqlite.OpenReadOnly(path);
        using NativeSqlite.SqliteStatement stmt = db.Prepare(
            "SELECT c1, c8, c9, c10, c13, c14 FROM conversations_fts_content WHERE c0 = ? AND c1 >= 0 ORDER BY c1;",
            conversationId);

        List<IndexedMessage> messages = [];
        while (stmt.Read())
        {
            string userOffsets = stmt.GetText(1);
            string assistantOffsets = stmt.GetText(2);
            string userBody = stmt.GetText(4);
            string assistantBody = stmt.GetText(5);

            messages.AddRange(ReadBody(userBody, userOffsets, CompactMessageKind.User));
            messages.AddRange(ReadBody(assistantBody, assistantOffsets, CompactMessageKind.Assistant));
        }

        return messages.OrderBy(m => m.StepIndex).ToList();
    }

    static IEnumerable<IndexedMessage> ReadBody(string body, string offsetsJson, CompactMessageKind kind)
    {
        if (body.Length == 0 || offsetsJson.Length == 0) yield break;
        byte[] bodyBytes = StrictUtf8.GetBytes(body);
        List<StepOffset> offsets = ParseOffsets(offsetsJson)
            .Where(offset => offset.ByteOffset >= 0 && offset.ByteOffset < bodyBytes.Length)
            .OrderBy(offset => offset.ByteOffset)
            .ThenBy(offset => offset.StepIndex)
            .ToList();

        for (int index = 0; index < offsets.Count; index++)
        {
            StepOffset offset = offsets[index];
            int end = bodyBytes.Length;
            for (int next = index + 1; next < offsets.Count; next++)
            {
                if (offsets[next].ByteOffset > offset.ByteOffset)
                {
                    end = offsets[next].ByteOffset;
                    break;
                }
            }
            if (end <= offset.ByteOffset) continue;

            string text = StrictUtf8.GetString(bodyBytes, offset.ByteOffset, end - offset.ByteOffset);
            if (kind == CompactMessageKind.User || !string.IsNullOrWhiteSpace(text))
                yield return new IndexedMessage(offset.StepIndex, kind, text);
        }
    }

    static List<StepOffset> ParseOffsets(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        List<StepOffset> offsets = [];
        foreach (JsonElement pair in doc.RootElement.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            offsets.Add(new StepOffset(pair[0].GetInt32(), pair[1].GetInt32()));
        }
        return offsets;
    }

    static DateTimeOffset? ParseIsoTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToLocalTime()
            : null;

    static string ParseFirstWorkspacePath(string workspaceUrisJson)
    {
        if (string.IsNullOrWhiteSpace(workspaceUrisJson)) return string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(workspaceUrisJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;
            foreach (JsonElement elem in doc.RootElement.EnumerateArray())
            {
                string? raw = elem.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                {
                    string unescaped = Uri.UnescapeDataString(uri.AbsolutePath);
                    if (unescaped.Length >= 3 && unescaped[0] == '/' && unescaped[2] == ':')
                        unescaped = char.ToUpperInvariant(unescaped[1]) + unescaped[2..];
                    return unescaped.Replace('/', Path.DirectorySeparatorChar);
                }
                return raw;
            }
        }
        catch { }
        return string.Empty;
    }

    readonly record struct StepOffset(int StepIndex, int ByteOffset);
    readonly record struct IndexedMessage(int StepIndex, CompactMessageKind Kind, string Text);
    readonly record struct ReasoningEntry(int StepIndex, DateTimeOffset? Timestamp, string Text);
    readonly record struct ToolEvent(int StepIndex, string ToolName);
    readonly record struct SummaryRow(string Title, DateTimeOffset? LastUserInput, DateTimeOffset? LastModified, int StepCount, string WorkspacePath, string ParentConversationId);

    sealed class StepScanResult
    {
        public int TotalSteps;
        public int GenerationSteps;
        public int ToolCallSteps;
        public int ActivitySteps;
        public int SummarySteps;

        public DateTimeOffset? EarliestTimestamp;
        public DateTimeOffset? LatestTimestamp;
        public Dictionary<int, DateTimeOffset?> StepTimestampsByIdx { get; } = [];

        public int MeasuredApiCalls;
        public long TotalNewInput;
        public long TotalOutput;
        public long TotalCacheHit;
        public long TotalReasoningOutput;
        public long LastContextTokens;
        public long? ContextWindowLimit;

        public Dictionary<string, int> ModelCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ToolNameCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ToolEvent> ToolEvents { get; } = [];
        public List<ReasoningEntry> ReasoningEntries { get; } = [];
    }
}
