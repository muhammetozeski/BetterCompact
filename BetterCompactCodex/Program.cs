using System.Text;

namespace BetterCompactCodex;

/// <summary>
/// Exports Codex (ChatGPT / GPT) rollout sessions into standardized Markdown files.
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
                    "BetterCompactCodex.exe",
                    "Codex / ChatGPT",
                    "Codex oturum UUID'si veya rollout .jsonl dosya yolu",
                    splitsByDefault: false);
                return ExitSuccess;
            }

            if (!CommonCommandLine.TryParse(arguments, defaultSplitOutput: false, out CommonOptions? options, out string error, out bool showHelp))
            {
                if (showHelp)
                {
                    CommonCommandLine.PrintHelp(
                        "BetterCompactCodex.exe",
                        "Codex / ChatGPT",
                        "Codex oturum UUID'si veya rollout .jsonl dosya yolu",
                        splitsByDefault: false);
                    return ExitSuccess;
                }
                ConsoleReport.Error(error);
                return ExitFailure;
            }

            OperationResult<bool> result = RunSafely(() => Execute(options!), operationName: "BetterCompactCodex");
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
            ConsoleReport.PrintSessionList("Codex", items, options.FindQuery);
            return;
        }

        string sessionPath = SessionLocator.ResolveSessionPath(options.Input);
        CompactDocument document = new TranscriptReader(options).Read(sessionPath);
        ExportPipeline.WriteAndReport(document, options);
    }
}
