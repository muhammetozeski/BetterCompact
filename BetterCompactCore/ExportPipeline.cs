using System.Text;

namespace BetterCompactCore;

/// <summary>
/// Orchestrates Markdown composition, optional multi-part splitting, disk writing, and reporting.
/// </summary>
public static class ExportPipeline
{
    const bool IsLogEnabled = true;
    static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes <paramref name="document"/> to disk under the target folder or NewTemp directory
    /// and prints the console report.
    /// </summary>
    public static List<string> WriteAndReport(CompactDocument document, CommonOptions options)
    {
        string baseName = FileNameBuilder.BuildBaseName(
            document.Metadata.HarnessFileToken,
            document.Metadata.PrimaryModelDisplay,
            document.Metadata.Title,
            document.Metadata.SessionId);

        (string outputDir, string? explicitFileName) = NewTempResolver.ResolveDestination(options.OutputDirectory, baseName);
        if (!string.IsNullOrWhiteSpace(explicitFileName))
            baseName = explicitFileName;

        List<ComposedPart> parts = MarkdownComposer.ComposeParts(document, options);
        List<string> writtenPaths = [];
        long totalBytes = 0;

        foreach (ComposedPart part in parts)
        {
            string fileName = part.TotalParts == 1
                ? $"{baseName}.md"
                : $"{baseName}_{part.PartNumber}.md";
            string fullPath = Path.Combine(outputDir, fileName);

            File.WriteAllText(fullPath, part.Content, Utf8NoBom);
            long size = new FileInfo(fullPath).Length;
            totalBytes += size;
            writtenPaths.Add(fullPath);

            Log($"Wrote part {part.PartNumber}/{part.TotalParts}; path={fullPath}; bytes={size}", isRun: IsLogEnabled);
        }

        ConsoleReport.PrintExportSummary(document, writtenPaths, totalBytes);
        return writtenPaths;
    }

    /// <summary>
    /// Trims an oversized single message safely on a UTF-8 character boundary.
    /// </summary>
    public static (string Text, bool WasTruncated) ApplySizeLimit(string text, CommonOptions options)
    {
        if (options.MaximumMessageBytes <= 0) return (text, false);

        int byteCount = Utf8NoBom.GetByteCount(text);
        if (byteCount <= options.MaximumMessageBytes) return (text, false);

        int openingBytes = Math.Min(options.OpeningBytes, options.MaximumMessageBytes);
        string dropped = $"[{ByteSize.Describe(byteCount)} mesajın geri kalanı kırpıldı: {options.MaximumMessageBytes} bayt sınırı aşıldı]";
        if (openingBytes <= 0) return (dropped, true);

        byte[] utf8 = Utf8NoBom.GetBytes(text);
        int cut = Math.Min(openingBytes, utf8.Length);
        while (cut > 0 && (utf8[cut] & 0xC0) == 0x80) cut--;
        string opening = Utf8NoBom.GetString(utf8, 0, cut);
        return ($"{opening}\n\n{dropped}", true);
    }
}
