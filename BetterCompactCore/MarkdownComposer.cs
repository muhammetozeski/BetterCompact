using System.Globalization;
using System.Text;

namespace BetterCompactCore;

/// <summary>Represents one numbered/rendered message block prior to optional multi-part splitting.</summary>
public sealed record RenderedBlock(string Content, string? MessageTag);

/// <summary>Represents one final Markdown output file ready to be written to disk.</summary>
public sealed record ComposedPart(int PartNumber, int TotalParts, string Content, string? FirstTag, string? LastTag);

/// <summary>
/// Composes standardized Turkish-Title-Case metadata headers, collapsible reasoning sections,
/// optional k1/a1 numbering + TOC placeholder, and safe multi-part splitting (44 KB / 700 lines).
/// </summary>
public static class MarkdownComposer
{
    const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    const string ShortDateFormat = "yyyy-MM-dd HH:mm";
    static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Renders <paramref name="document"/> into one or more Markdown parts according to <paramref name="options"/>.
    /// </summary>
    public static List<ComposedPart> ComposeParts(CompactDocument document, CommonOptions options)
    {
        List<RenderedBlock> blocks = RenderBodyBlocks(document, options);

        // Compute body byte size for the Size reduction metadata line.
        long bodyBytes = blocks.Sum(b => (long)Utf8NoBom.GetByteCount(b.Content));

        if (!options.SplitOutput)
        {
            string header = BuildPrimaryHeader(document, options, totalParts: 1, estimatedOutputBytes: bodyBytes + 1200);
            StringBuilder single = new(header.Length + (int)Math.Min(bodyBytes, int.MaxValue / 4));
            single.Append(header);
            foreach (RenderedBlock block in blocks) single.Append(block.Content);
            long finalBytes = Utf8NoBom.GetByteCount(single.ToString());

            // Refresh header once with exact byte size.
            string exactHeader = BuildPrimaryHeader(document, options, totalParts: 1, estimatedOutputBytes: finalBytes);
            StringBuilder exactSingle = new(exactHeader.Length + (int)Math.Min(bodyBytes, int.MaxValue / 4));
            exactSingle.Append(exactHeader);
            foreach (RenderedBlock block in blocks) exactSingle.Append(block.Content);

            string? firstTag = blocks.FirstOrDefault(b => b.MessageTag != null)?.MessageTag;
            string? lastTag = blocks.LastOrDefault(b => b.MessageTag != null)?.MessageTag;
            return [new ComposedPart(1, 1, exactSingle.ToString().ReplaceLineEndings("\r\n"), firstTag, lastTag)];
        }

        // First check whether everything fits inside a single part under the byte and line limits.
        string probeHeader = BuildPrimaryHeader(document, options, totalParts: 1, estimatedOutputBytes: bodyBytes + 1200);
        StringBuilder probe = new();
        probe.Append(probeHeader);
        foreach (RenderedBlock block in blocks) probe.Append(block.Content);
        string probeText = probe.ToString().ReplaceLineEndings("\r\n");
        if (FitsLimits(probeText, options.MaxPartBytes, options.MaxPartLines))
        {
            long exactBytes = Utf8NoBom.GetByteCount(probeText);
            string exactHeader = BuildPrimaryHeader(document, options, totalParts: 1, estimatedOutputBytes: exactBytes);
            StringBuilder exactSingle = new();
            exactSingle.Append(exactHeader);
            foreach (RenderedBlock block in blocks) exactSingle.Append(block.Content);
            string exactText = exactSingle.ToString().ReplaceLineEndings("\r\n");
            if (FitsLimits(exactText, options.MaxPartBytes, options.MaxPartLines))
            {
                string? firstTag = blocks.FirstOrDefault(b => b.MessageTag != null)?.MessageTag;
                string? lastTag = blocks.LastOrDefault(b => b.MessageTag != null)?.MessageTag;
                return [new ComposedPart(1, 1, exactText, firstTag, lastTag)];
            }
        }

        // Multi-part split respecting both byte and line limits.
        return SplitIntoParts(document, options, blocks, bodyBytes);
    }

    static List<ComposedPart> SplitIntoParts(
        CompactDocument document,
        CommonOptions options,
        List<RenderedBlock> blocks,
        long totalBodyBytes)
    {
        // Reserve a conservative header budget (exact part count is unknown until split completes).
        const int PartCountReserve = 999;
        string part1HeaderTemplate = BuildPrimaryHeader(document, options, totalParts: PartCountReserve, estimatedOutputBytes: totalBodyBytes + 2048);
        string contHeaderTemplate = BuildContinuationHeader(document, partNumber: PartCountReserve, totalParts: PartCountReserve, firstTag: "k9999", lastTag: "a9999");

        // Expand any single block that alone would exceed the available part budget.
        int maxPart1PayloadBytes = Math.Max(4096, options.MaxPartBytes - Utf8NoBom.GetByteCount(part1HeaderTemplate) - 256);
        int maxPart1PayloadLines = Math.Max(50, options.MaxPartLines - CountLines(part1HeaderTemplate) - 10);
        int maxContPayloadBytes = Math.Max(4096, options.MaxPartBytes - Utf8NoBom.GetByteCount(contHeaderTemplate) - 256);
        int maxContPayloadLines = Math.Max(50, options.MaxPartLines - CountLines(contHeaderTemplate) - 10);

        List<List<RenderedBlock>> buckets = [];
        List<RenderedBlock> currentBucket = [];
        int currentBytes = 0;
        int currentLines = 0;

        int CurrentByteBudget() => buckets.Count == 0 ? maxPart1PayloadBytes : maxContPayloadBytes;
        int CurrentLineBudget() => buckets.Count == 0 ? maxPart1PayloadLines : maxContPayloadLines;

        void FlushBucket()
        {
            if (currentBucket.Count == 0) return;
            buckets.Add(currentBucket);
            currentBucket = [];
            currentBytes = 0;
            currentLines = 0;
        }

        foreach (RenderedBlock block in blocks)
        {
            int blockBytes = Utf8NoBom.GetByteCount(block.Content.ReplaceLineEndings("\r\n"));
            int blockLines = CountLines(block.Content);

            if (currentBucket.Count > 0 &&
                (currentBytes + blockBytes > CurrentByteBudget() || currentLines + blockLines > CurrentLineBudget()))
            {
                FlushBucket();
            }

            if (blockBytes <= CurrentByteBudget() && blockLines <= CurrentLineBudget())
            {
                currentBucket.Add(block);
                currentBytes += blockBytes;
                currentLines += blockLines;
            }
            else
            {
                // Oversized single block: split cleanly along line boundaries while keeping code fences balanced.
                foreach (RenderedBlock piece in SplitOversizedBlock(block, CurrentByteBudget(), CurrentLineBudget()))
                {
                    int pieceBytes = Utf8NoBom.GetByteCount(piece.Content.ReplaceLineEndings("\r\n"));
                    int pieceLines = CountLines(piece.Content);
                    if (currentBucket.Count > 0 &&
                        (currentBytes + pieceBytes > CurrentByteBudget() || currentLines + pieceLines > CurrentLineBudget()))
                    {
                        FlushBucket();
                    }
                    currentBucket.Add(piece);
                    currentBytes += pieceBytes;
                    currentLines += pieceLines;
                    FlushBucket();
                }
            }
        }

        FlushBucket();
        if (buckets.Count == 0) buckets.Add([]);

        int totalParts = buckets.Count;
        long totalFinalBytes = totalBodyBytes + (totalParts * 600L);

        List<ComposedPart> result = [];
        for (int i = 0; i < totalParts; i++)
        {
            int partNumber = i + 1;
            List<RenderedBlock> bucket = buckets[i];
            string? firstTag = bucket.FirstOrDefault(b => b.MessageTag != null)?.MessageTag;
            string? lastTag = bucket.LastOrDefault(b => b.MessageTag != null)?.MessageTag;

            string header = partNumber == 1
                ? BuildPrimaryHeader(document, options, totalParts, totalFinalBytes)
                : BuildContinuationHeader(document, partNumber, totalParts, firstTag, lastTag);

            StringBuilder sb = new();
            sb.Append(header);
            foreach (RenderedBlock b in bucket) sb.Append(b.Content);

            result.Add(new ComposedPart(
                partNumber,
                totalParts,
                sb.ToString().ReplaceLineEndings("\r\n"),
                firstTag,
                lastTag));
        }

        return result;
    }

    static IEnumerable<RenderedBlock> SplitOversizedBlock(RenderedBlock block, int maxBytes, int maxLines)
    {
        string[] lines = block.Content.ReplaceLineEndings("\n").Split('\n');
        StringBuilder current = new();
        int currentBytes = 0;
        int currentLines = 0;
        string? openFence = null;

        int safeByteLimit = Math.Max(2048, maxBytes - 128);
        int safeLineLimit = Math.Max(20, maxLines - 6);

        foreach (string rawLine in lines)
        {
            string line = rawLine;
            int lineBytes = Utf8NoBom.GetByteCount(line) + 2;

            if (currentLines > 0 && (currentBytes + lineBytes > safeByteLimit || currentLines + 1 > safeLineLimit))
            {
                if (openFence != null) current.AppendLine("```");
                current.AppendLine();
                yield return new RenderedBlock(current.ToString(), block.MessageTag);

                current.Clear();
                currentBytes = 0;
                currentLines = 0;
                if (openFence != null)
                {
                    current.AppendLine(openFence);
                    currentBytes += Utf8NoBom.GetByteCount(openFence) + 2;
                    currentLines++;
                }
            }

            // Track fenced code blocks so splits stay valid Markdown.
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                openFence = openFence == null ? trimmed : null;
            }

            current.AppendLine(line);
            currentBytes += lineBytes;
            currentLines++;
        }

        if (current.Length > 0)
        {
            if (openFence != null) current.AppendLine("```");
            yield return new RenderedBlock(current.ToString(), block.MessageTag);
        }
    }

    static List<RenderedBlock> RenderBodyBlocks(CompactDocument document, CommonOptions options)
    {
        List<RenderedBlock> blocks = [];
        int userCounter = 0;
        int assistantCounter = 0;

        foreach (CompactMessage message in document.Messages)
        {
            if (message.Kind == CompactMessageKind.ToolActivity)
            {
                blocks.Add(new RenderedBlock($"> {message.Text}\n\n", null));
                continue;
            }

            if (message.Kind == CompactMessageKind.Reasoning)
            {
                blocks.Add(new RenderedBlock(FormatCollapsibleReasoning(message), null));
                continue;
            }

            string? tag = null;
            if (options.IncludeToc)
            {
                if (message.Kind is CompactMessageKind.User or CompactMessageKind.SlashCommand)
                    tag = $"k{++userCounter}";
                else if (message.Kind is CompactMessageKind.Assistant or CompactMessageKind.AssistantCommentary)
                    tag = $"a{++assistantCounter}";
            }

            StringBuilder sb = new();
            sb.AppendLine(HeadingFor(message, document.Metadata.AssistantDisplayName, tag));
            sb.AppendLine();
            sb.AppendLine(message.Text);
            sb.AppendLine();
            blocks.Add(new RenderedBlock(sb.ToString(), tag));
        }

        return blocks;
    }

    static string FormatCollapsibleReasoning(CompactMessage message)
    {
        string normalized = message.Text.ReplaceLineEndings("\n").Trim();
        int lineCount = normalized.Split('\n').Length;
        int byteCount = Utf8NoBom.GetByteCount(normalized);
        string timePart = message.Timestamp != null
            ? $" · {FormatMoment(message.Timestamp, TimestampFormat)}"
            : string.Empty;

        StringBuilder sb = new();
        sb.AppendLine("<details>");
        sb.AppendLine($"<summary><strong>Düşünme Süreci</strong> · {lineCount} satır · {ByteSize.Describe(byteCount)}{timePart}</summary>");
        sb.AppendLine();
        foreach (string line in normalized.Split('\n'))
        {
            sb.AppendLine(line.Length == 0 ? ">" : $"> {line}");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
        return sb.ToString();
    }

    static string BuildPrimaryHeader(CompactDocument document, CommonOptions options, int totalParts, long estimatedOutputBytes)
    {
        CompactMetadata meta = document.Metadata;
        CompactStatistics stats = document.Statistics;
        StringBuilder sb = new();

        sb.AppendLine($"# {meta.Title.ReplaceLineEndings(" ").Trim()}");
        sb.AppendLine();
        sb.AppendLine($"- **Sohbet UUID:** `{meta.SessionId}`");
        sb.AppendLine($"- **Sohbet Başlığı:** {meta.Title.ReplaceLineEndings(" ").Trim()}");
        sb.AppendLine($"- **Harness:** {meta.HarnessDisplayName}");
        sb.AppendLine($"- **Model:** {meta.PrimaryModelDisplay}");
        sb.AppendLine($"- **Model Ailesi:** {meta.ModelFamily}");

        if (meta.ModelShares.Count > 1)
        {
            string shareList = string.Join(", ", meta.ModelShares.Select(s => $"{s.DisplayName} ({ByteSize.FormatPercent(s.Percentage)})"));
            sb.AppendLine($"- **Kullanılan Modeller:** {shareList}");
        }

        if (!string.IsNullOrWhiteSpace(meta.ParentSessionId))
            sb.AppendLine($"- **Üst Sohbet UUID:** `{meta.ParentSessionId}`");

        if (!string.IsNullOrWhiteSpace(meta.WorkingDirectory))
            sb.AppendLine($"- **Çalışma Klasörü:** `{meta.WorkingDirectory}`");

        if (meta.StartedAt != null && meta.EndedAt != null)
        {
            TimeSpan duration = meta.EndedAt.Value - meta.StartedAt.Value;
            string durationText = duration.TotalSeconds >= 1 ? $" ({FormatDuration(duration)})" : string.Empty;
            sb.AppendLine($"- **Başlangıç / Bitiş:** {FormatMoment(meta.StartedAt, ShortDateFormat)} – {FormatMoment(meta.EndedAt, ShortDateFormat)}{durationText}");
        }
        else if (meta.EndedAt != null)
        {
            sb.AppendLine($"- **Son Etkinlik:** {FormatMoment(meta.EndedAt, ShortDateFormat)}");
        }

        sb.AppendLine($"- **Dışa Aktarım Tarihi:** {FormatMoment(meta.ExportedAt, ShortDateFormat)}");

        if (meta.TokenUsage is { HasData: true } usage)
        {
            if (usage.LastContextTokens > 0)
            {
                string contextLine = usage.ContextWindowLimit is > 0
                    ? $"{ByteSize.FormatNumber(usage.LastContextTokens)} / {ByteSize.FormatNumber(usage.ContextWindowLimit.Value)} ({ByteSize.FormatPercent(usage.LastContextTokens * 100.0 / usage.ContextWindowLimit.Value)})"
                    : ByteSize.FormatNumber(usage.LastContextTokens);
                sb.AppendLine($"- **Bağlam Doluluğu:** {contextLine}");
            }

            if (usage.BilledTotalTokens > 0 || usage.CacheReadTokens > 0)
            {
                List<string> breakdown = [];
                if (usage.BilledNewInputTokens > 0) breakdown.Add($"Yeni Giriş: {ByteSize.FormatNumber(usage.BilledNewInputTokens)}");
                if (usage.CacheWriteTokens > 0) breakdown.Add($"Cache Yazım: {ByteSize.FormatNumber(usage.CacheWriteTokens)}");
                if (usage.BilledOutputTokens > 0)
                {
                    string outputPart = usage.BilledReasoningOutputTokens > 0
                        ? $"Çıktı: {ByteSize.FormatNumber(usage.BilledOutputTokens)} (Düşünme: {ByteSize.FormatNumber(usage.BilledReasoningOutputTokens)})"
                        : $"Çıktı: {ByteSize.FormatNumber(usage.BilledOutputTokens)}";
                    breakdown.Add(outputPart);
                }

                string breakdownText = breakdown.Count > 0 ? $" ({string.Join(" · ", breakdown)})" : string.Empty;
                sb.AppendLine($"- **Harcanan Token (Cache Hariç):** {ByteSize.FormatNumber(usage.BilledTotalTokens)}{breakdownText}");

                if (usage.CacheReadTokens > 0)
                {
                    sb.AppendLine(
                        $"- **Cache Hit:** {ByteSize.FormatNumber(usage.CacheReadTokens)} " +
                        $"({ByteSize.FormatPercent(usage.CacheHitPercent)} · Cache Dahil Toplam: {ByteSize.FormatNumber(usage.GrossTotalTokens)})");
                }
            }
        }

        // Message & exclusion counts
        string commandPart = stats.SlashCommands > 0 ? $", {stats.SlashCommands} komut" : string.Empty;
        sb.AppendLine(
            $"- **Mesaj Sayısı:** {stats.UserMessages} kullanıcı{commandPart}, {stats.AssistantMessages} yanıt " +
            $"(toplam {ByteSize.FormatNumber(stats.TotalEntries)} {stats.EntryUnitName})");

        List<string> dropped = [];
        if (stats.ToolCalls > 0) dropped.Add($"{stats.ToolCalls} araç çağrısı");
        if (stats.ToolResults > 0) dropped.Add($"{stats.ToolResults} araç sonucu");
        if (!options.KeepReasoning && stats.ReasoningBlocks > 0) dropped.Add($"{stats.ReasoningBlocks} düşünme bloğu");
        if (!options.KeepInjected && stats.InjectedBlocks > 0) dropped.Add($"{stats.InjectedBlocks} enjekte blok");
        if (!options.KeepCompactSummaries && stats.CompactSummaries > 0) dropped.Add($"{stats.CompactSummaries} eski özet");
        if (stats.Attachments > 0) dropped.Add($"{stats.Attachments} ek");
        if (dropped.Count > 0)
            sb.AppendLine($"- **Çıkarılanlar:** {string.Join(", ", dropped)}");

        if (stats.TopTools.Count > 0)
        {
            string topTools = string.Join(", ", stats.TopTools.Take(5).Select(t => $"`{t.Key}` x{t.Value}"));
            sb.AppendLine($"- **En Çok Kullanılan Araçlar:** {topTools}");
        }

        if (meta.SourceSizeBytes > 0 && estimatedOutputBytes > 0)
        {
            double reduction = Math.Max(0, (1.0 - (estimatedOutputBytes / (double)meta.SourceSizeBytes)) * 100.0);
            sb.AppendLine($"- **Boyut:** {ByteSize.Describe(meta.SourceSizeBytes)} → {ByteSize.Describe(estimatedOutputBytes)} ({ByteSize.FormatPercent(reduction)} azalma)");
        }

        List<string> splitOrTrim = [];
        if (totalParts > 1) splitOrTrim.Add($"Parça 1/{totalParts}");
        if (stats.OversizedMessages > 0) splitOrTrim.Add($"Kırpılan Mesaj: {stats.OversizedMessages}");
        if (splitOrTrim.Count > 0)
            sb.AppendLine($"- **Parça / Kırpılma:** {string.Join(" · ", splitOrTrim)}");

        if (meta.SourcePaths.Count == 1)
        {
            sb.AppendLine($"- **Kaynak:** `{meta.SourcePaths[0]}`");
        }
        else if (meta.SourcePaths.Count > 1)
        {
            sb.AppendLine("- **Kaynaklar:**");
            foreach (string src in meta.SourcePaths)
                sb.AppendLine($"  - `{src}`");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (options.IncludeToc)
        {
            sb.AppendLine("İçindekiler:");
            sb.AppendLine("{");
            sb.AppendLine();
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    static string BuildContinuationHeader(
        CompactDocument document,
        int partNumber,
        int totalParts,
        string? firstTag,
        string? lastTag)
    {
        StringBuilder sb = new();
        sb.AppendLine($"# {document.Metadata.Title.ReplaceLineEndings(" ").Trim()} (Parça {partNumber}/{totalParts})");
        sb.AppendLine();
        sb.AppendLine($"- **Sohbet UUID:** `{document.Metadata.SessionId}`");
        string rangePart = firstTag != null && lastTag != null ? $" · Mesajlar: {firstTag} – {lastTag}" : string.Empty;
        sb.AppendLine($"- **Parça:** {partNumber}/{totalParts}{rangePart}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    static string HeadingFor(CompactMessage message, string assistantName, string? tag)
    {
        string prefix = tag != null ? $"[{tag}] " : string.Empty;
        string label = message.Kind switch
        {
            CompactMessageKind.User => $"## {prefix}Kullanıcı",
            CompactMessageKind.SlashCommand => $"## {prefix}Komut",
            CompactMessageKind.Assistant => $"## {prefix}{assistantName}",
            CompactMessageKind.AssistantCommentary => $"## {prefix}{assistantName} (Ara Not)",
            CompactMessageKind.Injected when message.Subtitle.Length > 0 => $"## Enjekte ({message.Subtitle})",
            CompactMessageKind.Injected => "## Enjekte",
            CompactMessageKind.CompactSummary => "## Eski Sıkıştırma Özeti",
            _ => $"## {prefix}Mesaj"
        };

        return message.Timestamp == null
            ? label
            : $"{label} · {FormatMoment(message.Timestamp, TimestampFormat)}";
    }

    static bool FitsLimits(string text, int maxBytes, int maxLines) =>
        Utf8NoBom.GetByteCount(text) <= maxBytes && CountLines(text) <= maxLines;

    static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int lines = 1;
        foreach (char c in text)
            if (c == '\n') lines++;
        return lines;
    }

    static string FormatMoment(DateTimeOffset? moment, string format) =>
        moment?.ToLocalTime().ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

    static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}g {duration.Hours}s {duration.Minutes}dk";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}s {duration.Minutes}dk";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}dk {duration.Seconds}sn";
        return $"{Math.Max(1, (int)duration.TotalSeconds)}sn";
    }
}
