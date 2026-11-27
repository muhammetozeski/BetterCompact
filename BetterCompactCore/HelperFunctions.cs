using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterCompactCore;

/// <summary>
/// Provides shared helper utilities for log rotation and string formatting.
/// </summary>
public static partial class HelperFunctions
{
    internal const bool IsLogEnabled = true;

    [GeneratedRegex("([A-Z0-9])")]
    private static partial Regex CamelCaseRegex();

    /// <summary>Inserts spaces before uppercase letters and digits in a PascalCase identifier.</summary>
    public static string SplitCamelCase(string input) => CamelCaseRegex().Replace(input, " $1").Trim();

    /// <summary>
    /// Removes the oldest timestamped log files in <paramref name="folderPath"/> so at most
    /// <paramref name="filesToKeep"/> files remain.
    /// </summary>
    public static void DeleteOldestFiles(string folderPath, int filesToKeep, string prefix = "")
    {
        if (!Directory.Exists(folderPath))
            return;

        List<(string Path, DateTime Date)> datedFiles = [];
        foreach (string filePath in Directory.GetFiles(folderPath))
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal))
                name = name[prefix.Length..];
            name = name.Trim();

            if (DateTime.TryParseExact(
                name,
                "yyyy.MM.dd HH.mm.ss.ff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime fileDate))
            {
                datedFiles.Add((filePath, fileDate));
            }
        }

        List<(string Path, DateTime Date)> sorted = datedFiles.OrderBy(file => file.Date).ToList();
        int filesToDeleteCount = sorted.Count - filesToKeep;
        if (filesToDeleteCount <= 0)
            return;

        for (int i = 0; i < filesToDeleteCount; i++)
        {
            try { File.Delete(sorted[i].Path); }
            catch { /* Ignore locked log files from concurrent runs. */ }
        }
    }
}
