using System.Globalization;

namespace BetterCompactCore;

/// <summary>Prints structured console summaries, file paths, and session listings.</summary>
public static class ConsoleReport
{
    public static void Info(string message) => Console.WriteLine(message);

    public static void Error(string message) => Console.Error.WriteLine($"Hata: {message}");

    /// <summary>Prints a standardized summary of the completed export and every generated file path.</summary>
    public static void PrintExportSummary(
        CompactDocument document,
        IReadOnlyList<string> writtenFiles,
        long totalOutputBytes)
    {
        CompactMetadata meta = document.Metadata;
        CompactStatistics stats = document.Statistics;

        Console.WriteLine($"Harness: {meta.HarnessDisplayName} | Model: {meta.PrimaryModelDisplay} ({meta.ModelFamily})");
        Console.WriteLine($"Başlık:  {meta.Title}");
        Console.WriteLine($"UUID:    {meta.SessionId}");
        Console.WriteLine(
            $"Mesaj:   {stats.UserMessages} kullanıcı, {stats.AssistantMessages} yanıt " +
            $"(toplam {ByteSize.FormatNumber(stats.TotalEntries)} {stats.EntryUnitName})");
        Console.WriteLine(
            $"Atılan:  {stats.ToolCalls} araç çağrısı, {stats.ToolResults} araç sonucu, " +
            $"{stats.ReasoningBlocks} düşünme, {stats.InjectedBlocks} enjekte, {stats.CompactSummaries} özet");

        if (meta.TokenUsage is { HasData: true } usage)
        {
            Console.WriteLine(
                $"Token:   Bağlam={ByteSize.FormatNumber(usage.LastContextTokens)} | " +
                $"Harcanan(CacheHariç)={ByteSize.FormatNumber(usage.BilledTotalTokens)} | " +
                $"CacheHit={ByteSize.FormatNumber(usage.CacheReadTokens)} ({ByteSize.FormatPercent(usage.CacheHitPercent)})");
        }

        if (meta.SourceSizeBytes > 0)
        {
            double reduction = Math.Max(0, (1.0 - (totalOutputBytes / (double)meta.SourceSizeBytes)) * 100.0);
            Console.WriteLine($"Boyut:   {ByteSize.Describe(meta.SourceSizeBytes)} -> {ByteSize.Describe(totalOutputBytes)} ({ByteSize.FormatPercent(reduction)} azalma)");
        }

        Console.WriteLine(writtenFiles.Count == 1 ? "Çıktı:" : $"Çıktılar ({writtenFiles.Count} parça):");
        foreach (string path in writtenFiles)
            Console.WriteLine(path);
    }

    /// <summary>Prints a compact table of discovered sessions for --list or --find.</summary>
    public static void PrintSessionList(string harnessName, IReadOnlyList<SessionSummaryItem> items, string? query = null)
    {
        if (items.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(query)
                ? $"{harnessName}: sohbet bulunamadı."
                : $"{harnessName}: '{query}' için eşleşen sohbet bulunamadı.");
            return;
        }

        Console.WriteLine(string.IsNullOrWhiteSpace(query)
            ? $"{harnessName} — Son {items.Count} sohbet:"
            : $"{harnessName} — '{query}' için {items.Count} eşleşme:");

        foreach (SessionSummaryItem item in items)
        {
            string date = item.LastActivity?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "---- -- -- --:--";
            string title = string.IsNullOrWhiteSpace(item.Title) ? "(Başlıksız)" : item.Title.ReplaceLineEndings(" ").Trim();
            Console.WriteLine($"{date} | {item.SessionId} | {title}");
        }
    }
}
