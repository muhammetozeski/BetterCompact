namespace BetterCompact;

/// <summary>
/// What to read, where to write it, and which of the normally-dropped parts to keep anyway.
/// </summary>
/// <param name="InputPath">A <c>.jsonl</c> session file, or a folder holding several of them.</param>
/// <param name="OutputPath">File or folder to write to. Empty writes beside the input.</param>
/// <param name="KeepInjected">Keep text that arrived in the user turn without anybody typing it: task notifications, skill bodies, cron prompts, resumption prompts.</param>
/// <param name="KeepThinking">Keep the assistant's reasoning blocks.</param>
/// <param name="KeepCompactSummaries">Keep the summaries written when a session ran out of context.</param>
/// <param name="KeepToolActivity">Keep the one-line tool count that stands between two messages.</param>
/// <param name="KeepSystemReminders">Leave <c>&lt;system-reminder&gt;</c> blocks inside messages instead of removing them.</param>
/// <param name="MaximumMessageBytes">Cut any single message longer than this many bytes down to its opening, with a line at the end saying what was dropped. Zero, the default, keeps every message whole however long it is.</param>
/// <param name="OpeningBytes">How much of a cut message is still written out, so it can be recognised. Never more than <paramref name="MaximumMessageBytes"/>, since keeping more than the limit would defeat it.</param>
sealed record CompactOptions(
    string InputPath,
    string OutputPath = "",
    bool KeepInjected = false,
    bool KeepThinking = false,
    bool KeepCompactSummaries = false,
    bool KeepToolActivity = true,
    bool KeepSystemReminders = false,
    int MaximumMessageBytes = 0,
    int OpeningBytes = CompactOptions.DefaultOpeningBytes)
{
    /// <summary>Enough of a message to tell which one it was, without carrying its bulk.</summary>
    public const int DefaultOpeningBytes = 512;
}

/// <summary>
/// Turns the command line into <see cref="CompactOptions"/>, and holds the help text those options
/// are documented in. Adding a switch means touching this class and nothing else.
/// </summary>
static class CommandLine
{
    /// <summary>
    /// Reads the arguments into options.
    /// </summary>
    /// <param name="arguments">The raw command line.</param>
    /// <param name="options">The parsed options, or null when parsing failed or help was asked for.</param>
    /// <param name="error">Why parsing failed, or an empty string when it did not.</param>
    /// <returns>True when the caller should go on and compact; false when it should stop.</returns>
    public static bool TryParse(string[] arguments, out CompactOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        string inputPath = string.Empty;
        string outputPath = string.Empty;
        bool keepInjected = false;
        bool keepThinking = false;
        bool keepCompactSummaries = false;
        bool keepToolActivity = true;
        bool keepSystemReminders = false;
        int maximumMessageBytes = 0;
        int openingBytes = CompactOptions.DefaultOpeningBytes;

        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "-h" or "--help" or "/?":
                    PrintHelp();
                    return false;

                case "-o" or "--output":
                    if (index + 1 >= arguments.Length)
                    {
                        error = $"'{argument}' needs a path after it.";
                        return false;
                    }
                    outputPath = arguments[++index].Trim('"');
                    break;

                case "--everything":
                    keepInjected = true;
                    keepThinking = true;
                    keepCompactSummaries = true;
                    keepSystemReminders = true;
                    break;

                case "--max-message":
                    if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out maximumMessageBytes) || maximumMessageBytes < 1)
                    {
                        error = $"'{argument}' needs a size in bytes after it, for example --max-message 4096.";
                        return false;
                    }
                    index++;
                    break;

                case "--keep-opening":
                    if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out openingBytes) || openingBytes < 0)
                    {
                        error = $"'{argument}' needs a size in bytes after it, for example --keep-opening 512.";
                        return false;
                    }
                    index++;
                    break;

                case "--injected": keepInjected = true; break;
                case "--thinking": keepThinking = true; break;
                case "--summaries": keepCompactSummaries = true; break;
                case "--reminders": keepSystemReminders = true; break;
                case "--no-tools": keepToolActivity = false; break;

                default:
                    if (argument.StartsWith('-'))
                    {
                        error = $"Unknown option '{argument}'. Run with --help to see the list.";
                        return false;
                    }
                    if (inputPath.Length > 0)
                    {
                        error = "Only one input path is accepted.";
                        return false;
                    }
                    inputPath = argument.Trim('"');
                    break;
            }
        }

        if (inputPath.Length == 0)
        {
            error = "No input path given.";
            return false;
        }

        options = new CompactOptions(
            inputPath,
            outputPath,
            keepInjected,
            keepThinking,
            keepCompactSummaries,
            keepToolActivity,
            keepSystemReminders,
            maximumMessageBytes,
            openingBytes);
        return true;
    }

    /// <summary>
    /// Writes the usage text to the console.
    /// </summary>
    public static void PrintHelp() => Console.WriteLine("""

        BetterCompact - keeps the conversation from a Claude Code session file and drops the tool traffic

        USAGE:
          BetterCompact <session.jsonl> [-o <output>] [options]
          BetterCompact <folder>        [-o <folder>] [options]
          BetterCompact                                            (interactive)

        A session file is mostly tool inputs and tool outputs. Everything the two sides said to each
        other is a small fraction of it, and that fraction is what this keeps - whole, never shortened.

        OPTIONS:
          -o, --output <path>   Output file, or folder to put the output in. Default: beside the input.
              --injected        Also keep text nobody typed: task notifications, skill bodies, cron
                                prompts, "continue from where you left off".
              --thinking        Also keep the assistant's reasoning blocks.
              --summaries       Also keep the summaries written when a session ran out of context.
              --reminders       Leave <system-reminder> blocks inside messages.
              --no-tools        Drop the one-line tool counts between messages as well.
              --everything      All of the above except --no-tools.
              --max-message <n> Cut any single message longer than n BYTES down to its opening, with a
                                line at the end naming what was dropped. Off by default, so a long
                                message stays whole. Use it when somebody pasted a whole transcript
                                into the prompt and that one message is now most of the output.
              --keep-opening <n>
                                How much of a cut message is still written out, in bytes. Default 512.
                                Zero writes only the line naming what was dropped. Never more than
                                --max-message, since keeping more than the limit would defeat it.
          -h, --help            This text.

        EXAMPLES:
          BetterCompact session.jsonl
          BetterCompact session.jsonl -o C:\Notes\conversation.md
          BetterCompact "%USERPROFILE%\.claude\projects\my-project" -o C:\Notes
          BetterCompact session.jsonl --everything
          BetterCompact session.jsonl --max-message 4096

        """);
}
