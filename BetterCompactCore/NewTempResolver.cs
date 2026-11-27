using System.Diagnostics;

namespace BetterCompactCore;

/// <summary>
/// Obtains a fresh temporary output directory through NewTemp.exe, falling back to %TEMP% if unavailable.
/// </summary>
public static class NewTempResolver
{
    const bool IsLogEnabled = true;

    /// <summary>
    /// Resolves the destination directory or explicit file path for a BetterCompact export.
    /// </summary>
    public static (string DirectoryPath, string? ExplicitFileName) ResolveDestination(string requestedOutput, string topicFolderName)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutput))
        {
            string expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedOutput.Trim('"')));
            if (expanded.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(expanded) ?? Environment.CurrentDirectory;
                Directory.CreateDirectory(dir);
                return (dir, Path.GetFileNameWithoutExtension(expanded));
            }

            Directory.CreateDirectory(expanded);
            return (expanded, null);
        }

        string? newTempDir = TryCreateViaNewTemp(topicFolderName);
        if (!string.IsNullOrEmpty(newTempDir))
        {
            Directory.CreateDirectory(newTempDir);
            return (newTempDir, null);
        }

        string fallback = Path.Combine(
            Path.GetTempPath(),
            "BetterCompact",
            "Output",
            $"{topicFolderName}_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(fallback);
        Log($"NewTemp unavailable; using fallback output directory={fallback}", isRun: IsLogEnabled);
        return (fallback, null);
    }

    static string? TryCreateViaNewTemp(string folderName)
    {
        string safeName = FileNameBuilder.ToPascalSegment(folderName, 40, "BetterCompact");
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "NewTemp",
                Arguments = safeName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { }
                Log($"NewTemp timed out; stderr={stderr}", isRun: IsLogEnabled);
                return null;
            }

            if (process.ExitCode != 0)
            {
                Log($"NewTemp exited with code={process.ExitCode}; stderr={stderr}", isRun: IsLogEnabled);
                return null;
            }

            // NewTemp prints the created folder path (sometimes across lines); pick the line that points to an existing directory.
            string[] lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string candidate = lines[i].Trim('"');
                if (Directory.Exists(candidate))
                {
                    Log($"NewTemp created output directory={candidate}", isRun: IsLogEnabled);
                    return candidate;
                }
            }

            if (lines.Length > 0)
            {
                string last = lines[^1].Trim('"');
                if (Path.IsPathRooted(last))
                    return last;
            }
        }
        catch (Exception ex)
        {
            Log($"NewTemp invocation failed: {ex.Message}", isRun: IsLogEnabled);
        }

        return null;
    }
}
