using System.Globalization;

namespace BetterCompact;

/// <summary>
/// Everything the program prints. Colour and prefixes live here so no other class touches the console
/// directly.
/// </summary>
static class ConsoleReport
{
    const int PercentageScale = 100;

    /// <summary>Writes a step the program is taking.</summary>
    /// <param name="message">The line to write.</param>
    public static void Info(string message) => Write("[*] ", ConsoleColor.DarkCyan, message);

    /// <summary>Writes a step that finished as intended.</summary>
    /// <param name="message">The line to write.</param>
    public static void Success(string message) => Write("[+] ", ConsoleColor.Green, message);

    /// <summary>Writes something that went wrong but did not stop the run.</summary>
    /// <param name="message">The line to write.</param>
    public static void Warning(string message) => Write("[!] ", ConsoleColor.Yellow, message);

    /// <summary>Writes a failure.</summary>
    /// <param name="message">The line to write.</param>
    public static void Error(string message) => Write("[!] ", ConsoleColor.Red, message);

    /// <summary>
    /// Prints what was kept, what was dropped, and how much smaller the result is.
    /// </summary>
    /// <param name="statistics">The counts the reader produced.</param>
    /// <param name="inputBytes">Size of the session file.</param>
    /// <param name="outputBytes">Size of the Markdown that came out of it.</param>
    public static void Summarise(TranscriptStatistics statistics, long inputBytes, long outputBytes)
    {
        Console.WriteLine();
        Console.WriteLine("  kept");
        Console.WriteLine($"    user messages     {statistics.UserMessages}");
        Console.WriteLine($"    slash commands    {statistics.SlashCommands}");
        Console.WriteLine($"    replies           {statistics.AssistantMessages}");
        Console.WriteLine("  dropped");
        Console.WriteLine($"    tool calls        {statistics.ToolCalls}");
        Console.WriteLine($"    tool results      {statistics.ToolResults}");
        Console.WriteLine($"    injected          {statistics.InjectedMessages}");
        Console.WriteLine($"    thinking          {statistics.ThinkingBlocks}");
        Console.WriteLine($"    compact summaries {statistics.CompactSummaries}");
        Console.WriteLine($"    attachments       {statistics.Attachments}");

        if (statistics.DuplicateEntries > 0) Console.WriteLine($"    repeated entries  {statistics.DuplicateEntries}");
        if (statistics.OversizedMessages > 0) Console.WriteLine($"    over the limit    {statistics.OversizedMessages}");
        if (statistics.UnreadableLines > 0) Warning($"{statistics.UnreadableLines} lines were not valid JSON and were skipped.");

        double ratio = inputBytes == 0 ? 0 : (double)outputBytes / inputBytes * PercentageScale;
        Console.WriteLine();
        Success($"{ByteSize.Describe(inputBytes)} -> {ByteSize.Describe(outputBytes)}  ({ratio.ToString("0.#", CultureInfo.InvariantCulture)}% of the original)");
    }

    static void Write(string prefix, ConsoleColor colour, string message)
    {
        Console.ForegroundColor = colour;
        Console.Write(prefix);
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
