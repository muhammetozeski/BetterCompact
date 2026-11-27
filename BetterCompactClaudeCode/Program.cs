using System.Text;

namespace BetterCompactClaudeCode;

/// <summary>
/// Exports Claude Code sessions into standardized Markdown files.
/// </summary>
static class Program
{
    const int ExitSuccess = 0;
    const int ExitFailure = 1;

    static int Main(string[] arguments)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        try
        {
            if (arguments.Length == 0)
            {
                CommonCommandLine.PrintHelp(
                    "BetterCompactClaudeCode.exe",
                    "Claude Code / Claude",
                    "Claude Code oturum UUID'si veya .jsonl dosya yolu",
                    splitsByDefault: false);
                return ExitSuccess;
            }

            if (!CommonCommandLine.TryParse(arguments, defaultSplitOutput: false, out CommonOptions? options, out string error, out bool showHelp))
            {
                if (showHelp)
                {
                    CommonCommandLine.PrintHelp(
                        "BetterCompactClaudeCode.exe",
                        "Claude Code / Claude",
                        "Claude Code oturum UUID'si veya .jsonl dosya yolu",
                        splitsByDefault: false);
                    return ExitSuccess;
                }
                ConsoleReport.Error(error);
                return ExitFailure;
            }

            OperationResult<bool> result = RunSafely(() => Execute(options!), operationName: "BetterCompactClaudeCode");
            if (!result.Succeeded)
            {
                ConsoleReport.Error(result.Error?.Message ?? "Bilinmeyen hata.");
                return ExitFailure;
            }
            return ExitSuccess;
        }
        finally
        {
            Logger.Flush();
        }
    }

    static void Execute(CommonOptions options)
    {
        if (options.ListMode)
        {
            List<SessionSummaryItem> items = SessionLocator.ListSessions(options.ListLimit, options.FindQuery);
            ConsoleReport.PrintSessionList("Claude Code", items, options.FindQuery);
            return;
        }

        string sessionPath = SessionLocator.ResolveSessionPath(options.Input);
        CompactDocument document = new TranscriptReader(options).Read(sessionPath);
        ExportPipeline.WriteAndReport(document, options);
    }
}
