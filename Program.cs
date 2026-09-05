using System.Text;

namespace BetterCompact;

/// <summary>
/// Entry point: works out what to read, reads it, writes the Markdown, and reports what happened.
/// </summary>
static class Program
{
    const string SessionFileExtension = ".jsonl";
    const string OutputSuffix = ".compact.md";

    const int ExitSuccess = 0;
    const int ExitFailure = 1;

    static int Main(string[] arguments)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool interactive = arguments.Length == 0;

        CompactOptions? options;
        string error;

        if (interactive)
        {
            options = AskForOptions();
            if (options == null) return WaitAndReturn(ExitFailure, interactive);
        }
        else if (!CommandLine.TryParse(arguments, out options, out error))
        {
            if (error.Length > 0) ConsoleReport.Error(error);
            return error.Length > 0 ? ExitFailure : ExitSuccess;
        }

        try
        {
            return WaitAndReturn(Run(options!), interactive);
        }
        catch (Exception failure)
        {
            ConsoleReport.Error(failure.Message);
            return WaitAndReturn(ExitFailure, interactive);
        }
    }

    /// <summary>
    /// Compacts whatever the input path points at: one session file, or every session file in a folder.
    /// </summary>
    /// <param name="options">What to read and what to keep.</param>
    /// <returns>The process exit code.</returns>
    static int Run(CompactOptions options)
    {
        string inputPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.InputPath));

        if (Directory.Exists(inputPath))
        {
            string[] sessionFiles = Directory.GetFiles(inputPath, $"*{SessionFileExtension}");
            if (sessionFiles.Length == 0)
            {
                ConsoleReport.Error($"No {SessionFileExtension} files in {inputPath}");
                return ExitFailure;
            }

            ConsoleReport.Info($"{sessionFiles.Length} session files found.");
            foreach (string sessionFile in sessionFiles.OrderBy(File.GetLastWriteTimeUtc))
            {
                Console.WriteLine();
                CompactOne(sessionFile, options);
            }
            return ExitSuccess;
        }

        if (!File.Exists(inputPath))
        {
            ConsoleReport.Error($"Not found: {inputPath}");
            return ExitFailure;
        }

        CompactOne(inputPath, options);
        return ExitSuccess;
    }

    static void CompactOne(string sessionFile, CompactOptions options)
    {
        ConsoleReport.Info($"Reading {sessionFile}");

        TranscriptDocument document = new TranscriptReader(options).Read(sessionFile);
        string markdown = MarkdownComposer.Compose(document).ReplaceLineEndings("\r\n");

        string outputFile = ResolveOutputPath(sessionFile, options.OutputPath);
        string? outputFolder = Path.GetDirectoryName(outputFile);
        if (outputFolder is { Length: > 0 }) Directory.CreateDirectory(outputFolder);

        File.WriteAllText(outputFile, markdown, new UTF8Encoding(false));

        ConsoleReport.Summarise(document.Statistics, new FileInfo(sessionFile).Length, new FileInfo(outputFile).Length);
        ConsoleReport.Success($"Written to {outputFile}");
    }

    /// <summary>
    /// Works out where the Markdown for one session file goes.
    /// </summary>
    /// <param name="sessionFile">The session file being compacted.</param>
    /// <param name="requestedOutput">What the user passed to <c>-o</c>, or an empty string.</param>
    /// <returns>The full path to write to. An empty request, or one naming a folder, puts the file
    /// beside the session file or inside that folder under the session's own name.</returns>
    static string ResolveOutputPath(string sessionFile, string requestedOutput)
    {
        string defaultName = Path.GetFileNameWithoutExtension(sessionFile) + OutputSuffix;

        if (requestedOutput.Length == 0)
            return Path.Combine(Path.GetDirectoryName(sessionFile) ?? Directory.GetCurrentDirectory(), defaultName);

        string resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedOutput));
        return Directory.Exists(resolved) ? Path.Combine(resolved, defaultName) : resolved;
    }

    /// <summary>
    /// Asks for the paths when the program was started with no arguments, which is what a double-click
    /// does.
    /// </summary>
    /// <returns>The options to run with, or null when no input path was given.</returns>
    static CompactOptions? AskForOptions()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("BetterCompact");
        Console.ResetColor();
        Console.WriteLine("Keeps the conversation from a Claude Code session file and drops the tool traffic.");
        Console.WriteLine();

        string? inputPath = Ask("Session .jsonl file or folder: ");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            ConsoleReport.Error("An input path is needed.");
            return null;
        }

        string outputPath = Ask("Output file or folder (blank puts it beside the input): ") ?? string.Empty;
        bool keepEverything = (Ask("Keep injected text, thinking and summaries too? [y/N]: ") ?? string.Empty)
            .StartsWith('y');

        Console.WriteLine();
        return new CompactOptions(
            inputPath,
            outputPath,
            KeepInjected: keepEverything,
            KeepThinking: keepEverything,
            KeepCompactSummaries: keepEverything,
            KeepSystemReminders: keepEverything);
    }

    static string? Ask(string question)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(question);
        Console.ResetColor();
        return Console.ReadLine()?.Trim().Trim('"');
    }

    static int WaitAndReturn(int exitCode, bool interactive)
    {
        if (!interactive) return exitCode;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press any key to close...");
        Console.ResetColor();
        Console.ReadKey(true);
        return exitCode;
    }
}
