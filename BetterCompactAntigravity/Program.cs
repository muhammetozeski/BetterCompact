using System.Text;

namespace BetterCompactAntigravity;

/// <summary>
/// Exports Antigravity (Gemini) conversations into standardized, optionally split Markdown files.
/// </summary>
static class Program
{
    const int ExitSuccess = 0;
    const int ExitFailure = 1;
    const string DatabaseExtension = ".db";

    static int Main(string[] arguments)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        try
        {
            if (arguments.Length == 0)
            {
                CommonCommandLine.PrintHelp(
                    "BetterCompactAntigravity.exe",
                    "Antigravity / Gemini",
                    "Antigravity sohbet UUID'si veya .db dosya yolu",
                    splitsByDefault: true);
                return ExitSuccess;
            }

            if (!CommonCommandLine.TryParse(arguments, defaultSplitOutput: true, out CommonOptions? options, out string error, out bool showHelp))
            {
                if (showHelp)
                {
                    CommonCommandLine.PrintHelp(
                        "BetterCompactAntigravity.exe",
                        "Antigravity / Gemini",
                        "Antigravity sohbet UUID'si veya .db dosya yolu",
                        splitsByDefault: true);
                    return ExitSuccess;
                }
                ConsoleReport.Error(error);
                return ExitFailure;
            }

            OperationResult<bool> result = RunSafely(() => Execute(options!), operationName: "BetterCompactAntigravity");
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
            List<SessionSummaryItem> items = ConversationReader.ListSessions(options.ListLimit, options.FindQuery);
            ConsoleReport.PrintSessionList("Antigravity", items, options.FindQuery);
            return;
        }

        string input = Environment.ExpandEnvironmentVariables(options.Input).Trim('"');
        string conversationId = ResolveConversationId(input);
        string databasePath = File.Exists(input) && input.EndsWith(DatabaseExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(input)
            : Path.Combine(ConversationReader.DefaultAntigravityFolder(), "conversations", conversationId + DatabaseExtension);

        CompactDocument document = new ConversationReader(options).Read(conversationId, databasePath);
        ExportPipeline.WriteAndReport(document, options);
    }

    static string ResolveConversationId(string input)
    {
        string candidate = File.Exists(input) && input.EndsWith(DatabaseExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(input)
            : input;

        if (!Guid.TryParse(candidate, out Guid parsed))
            throw new ArgumentException($"Geçerli bir Antigravity sohbet UUID'si veya .db yolu değil: {input}");

        return parsed.ToString("D");
    }
}
