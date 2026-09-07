using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterCompact;

/// <summary>
/// Reads one Claude Code <c>.jsonl</c> session file into the ordered list of things the two sides said.
/// Tool inputs and tool outputs are counted rather than kept, which is where almost all of a session
/// file's size sits.
/// </summary>
sealed partial class TranscriptReader
{
    // A user entry carries origin.kind = "human" when the person typed it themselves. Anything else in
    // the user turn - a task notification, a skill body, a cron prompt - was put there by the tool.
    const string HumanOrigin = "human";

    // Text the tool wraps around things it injected. Used for session files old enough to predate the
    // origin field, where these tags are the only signal left.
    static readonly string[] InjectedOpeningTags =
    [
        "<task-notification>",
        "<local-command-caveat>",
        "<local-command-stdout>",
        "<user-memory-input>",
        "<ide_opened_file>",
        "<ci-monitor-event>"
    ];

    // The opening sentence of the handoff a session writes when it runs out of context.
    const string CompactHandoffOpening = "This session is being continued from a previous conversation";

    const string CommandMessageOpen = "<command-message>";
    const string CommandNameOpen = "<command-name>";
    const string CommandNameClose = "</command-name>";
    const string CommandArgumentsOpen = "<command-args>";
    const string CommandArgumentsClose = "</command-args>";

    // The client stamps this on a message the user sent while quoting an earlier one. It is markup, not
    // something they typed.
    const string QuoteReplyMarker = "<!-- attach -->";

    // A UTF-8 byte that continues the character before it looks like 10xxxxxx.
    const int ContinuationByteMask = 0xC0;
    const int ContinuationByteValue = 0x80;

    readonly CompactOptions options;
    readonly HashSet<string> seenEntryIds = [];
    readonly List<TranscriptMessage> messages = [];
    readonly Dictionary<string, int> pendingToolCalls = [];

    int totalLines;
    int unreadableLines;
    int duplicateEntries;
    int userMessages;
    int slashCommands;
    int assistantMessages;
    int thinkingBlocks;
    int injectedMessages;
    int compactSummaries;
    int toolCalls;
    int toolResults;
    int attachments;
    int oversizedMessages;
    int pendingToolResults;

    string title = string.Empty;
    string sessionId = string.Empty;

    /// <summary>
    /// Creates a reader bound to the choices the user made on the command line.
    /// </summary>
    /// <param name="options">Which normally-dropped parts to keep.</param>
    public TranscriptReader(CompactOptions options) => this.options = options;

    /// <summary>
    /// Reads the file at <paramref name="path"/> from start to end.
    /// </summary>
    /// <param name="path">Full path of the session file.</param>
    /// <returns>What the session was called, what was said in it, and the counts behind that.</returns>
    public TranscriptDocument Read(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            totalLines++;

            JsonDocument? parsed = null;
            try { parsed = JsonDocument.Parse(line); }
            catch (JsonException) { unreadableLines++; }

            if (parsed == null) continue;
            using (parsed) ReadEntry(parsed.RootElement);
        }

        FlushToolActivity();

        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path);

        TranscriptStatistics statistics = new(
            totalLines, unreadableLines, duplicateEntries,
            userMessages, slashCommands, assistantMessages, thinkingBlocks,
            injectedMessages, compactSummaries, toolCalls, toolResults, attachments, oversizedMessages);

        return new TranscriptDocument(title, sessionId, messages, statistics);
    }

    void ReadEntry(JsonElement entry)
    {
        if (sessionId.Length == 0) sessionId = TextOf(entry, "sessionId") ?? string.Empty;

        string? entryType = TextOf(entry, "type");
        switch (entryType)
        {
            case "ai-title":
                title = TextOf(entry, "aiTitle") ?? title;
                return;

            case "custom-title":
                if (title.Length == 0) title = TextOf(entry, "customTitle") ?? string.Empty;
                return;

            case "attachment":
                attachments++;
                return;

            case "user":
                ReadUserEntry(entry);
                return;

            case "assistant":
                ReadAssistantEntry(entry);
                return;
        }
    }

    void ReadUserEntry(JsonElement entry)
    {
        if (IsRepeat(entry)) return;

        JsonElement? content = ContentOf(entry);
        if (content == null) return;
        DateTimeOffset? timestamp = TimestampOf(entry);

        if (content.Value.ValueKind == JsonValueKind.String)
        {
            AddUserText(entry, content.Value.GetString() ?? string.Empty, timestamp);
            return;
        }

        if (content.Value.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement block in content.Value.EnumerateArray())
        {
            switch (TextOf(block, "type"))
            {
                case "text":
                    AddUserText(entry, TextOf(block, "text") ?? string.Empty, timestamp);
                    break;

                case "tool_result":
                    toolResults++;
                    pendingToolResults++;
                    break;
            }
        }
    }

    void ReadAssistantEntry(JsonElement entry)
    {
        if (IsRepeat(entry)) return;

        JsonElement? content = ContentOf(entry);
        if (content == null || content.Value.ValueKind != JsonValueKind.Array) return;
        DateTimeOffset? timestamp = TimestampOf(entry);

        foreach (JsonElement block in content.Value.EnumerateArray())
        {
            switch (TextOf(block, "type"))
            {
                case "text":
                    string reply = (TextOf(block, "text") ?? string.Empty).Trim();
                    if (reply.Length == 0) break;
                    assistantMessages++;
                    Add(MessageKind.Assistant, timestamp, reply);
                    break;

                case "thinking":
                    string reasoning = (TextOf(block, "thinking") ?? string.Empty).Trim();
                    if (reasoning.Length == 0) break;
                    thinkingBlocks++;
                    if (options.KeepThinking) Add(MessageKind.Thinking, timestamp, reasoning);
                    break;

                case "tool_use":
                    toolCalls++;
                    string toolName = TextOf(block, "name") ?? "unknown";
                    pendingToolCalls[toolName] = pendingToolCalls.GetValueOrDefault(toolName) + 1;
                    break;
            }
        }
    }

    void AddUserText(JsonElement entry, string rawText, DateTimeOffset? timestamp)
    {
        string text = rawText.Trim();
        if (text.Length == 0) return;

        switch (KindOf(entry, text))
        {
            case MessageKind.SlashCommand:
                slashCommands++;
                Add(MessageKind.SlashCommand, timestamp, FlattenSlashCommand(text));
                break;

            case MessageKind.Injected:
                injectedMessages++;
                if (options.KeepInjected) Add(MessageKind.Injected, timestamp, text);
                break;

            case MessageKind.CompactSummary:
                compactSummaries++;
                if (options.KeepCompactSummaries) Add(MessageKind.CompactSummary, timestamp, text);
                break;

            default:
                userMessages++;
                Add(MessageKind.User, timestamp, Clean(text));
                break;
        }
    }

    /// <summary>
    /// Decides which part of the conversation a piece of user-turn text came from.
    /// </summary>
    /// <param name="entry">The whole session entry, for the fields that mark its source.</param>
    /// <param name="text">The trimmed text of the entry.</param>
    /// <returns>The kind to file it under. Text with no marker at all counts as the person's own, so a
    /// session file written before those markers existed never loses a message.</returns>
    static MessageKind KindOf(JsonElement entry, string text)
    {
        bool isCommandEntry = IsCommandEntry(text);

        if (entry.TryGetProperty("origin", out JsonElement origin) && origin.ValueKind == JsonValueKind.Object)
            return TextOf(origin, "kind") == HumanOrigin
                ? isCommandEntry ? MessageKind.SlashCommand : MessageKind.User
                : MessageKind.Injected;

        if (entry.TryGetProperty("isMeta", out JsonElement isMeta) && isMeta.ValueKind == JsonValueKind.True)
            return MessageKind.Injected;

        if (entry.TryGetProperty("isCompactSummary", out JsonElement isSummary) && isSummary.ValueKind == JsonValueKind.True)
            return MessageKind.CompactSummary;

        if (text.StartsWith(CompactHandoffOpening, StringComparison.Ordinal))
            return MessageKind.CompactSummary;

        if (isCommandEntry) return MessageKind.SlashCommand;

        foreach (string tag in InjectedOpeningTags)
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                return MessageKind.Injected;

        return MessageKind.User;
    }

    /// <summary>
    /// Tells whether an entry is the record of a slash command rather than ordinary text.
    /// </summary>
    /// <param name="text">The trimmed text of the entry.</param>
    /// <returns>True only when the command markup opens the entry. Merely containing the markup is not
    /// enough: somebody who pastes an old transcript into the prompt pastes those tags along with it,
    /// and reading that as a command would throw the rest of their message away.</returns>
    static bool IsCommandEntry(string text) =>
        text.StartsWith(CommandMessageOpen, StringComparison.Ordinal) ||
        text.StartsWith(CommandNameOpen, StringComparison.Ordinal);

    /// <summary>
    /// Reduces the markup around a slash command to the line the person typed.
    /// </summary>
    /// <param name="text">The entry's text, holding the command tags.</param>
    /// <returns>The command name followed by its arguments, or the original text when no name was found.</returns>
    static string FlattenSlashCommand(string text)
    {
        string? name = Between(text, CommandNameOpen, CommandNameClose);
        if (name == null) return text;

        string? arguments = Between(text, CommandArgumentsOpen, CommandArgumentsClose);
        return arguments is { Length: > 0 } ? $"{name.Trim()}\n{arguments.Trim()}" : name.Trim();
    }

    string Clean(string text)
    {
        if (!options.KeepSystemReminders) text = SystemReminderBlock().Replace(text, string.Empty);
        return text.Replace(QuoteReplyMarker, string.Empty).Trim();
    }

    void Add(MessageKind kind, DateTimeOffset? timestamp, string text)
    {
        if (text.Length == 0) return;
        FlushToolActivity();
        messages.Add(new TranscriptMessage(kind, timestamp, WithinSizeLimit(text)));
    }

    /// <summary>
    /// Applies the size limit the caller set, if they set one.
    /// </summary>
    /// <param name="text">The message as it stood in the session file.</param>
    /// <returns>The message unchanged, or its opening followed by a line naming what was dropped. The
    /// line is what keeps a cut message from reading as a whole one.</returns>
    string WithinSizeLimit(string text)
    {
        if (options.MaximumMessageBytes == 0) return text;

        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount <= options.MaximumMessageBytes) return text;

        oversizedMessages++;
        int openingBytes = Math.Min(options.OpeningBytes, options.MaximumMessageBytes);
        string dropped = $"[rest of a {ByteSize.Describe(byteCount)} message dropped: over the {options.MaximumMessageBytes} byte limit]";
        return openingBytes == 0 ? dropped : $"{Opening(text, openingBytes)}\n\n{dropped}";
    }

    /// <summary>
    /// Takes the start of a message without splitting a character in half.
    /// </summary>
    /// <param name="text">The message to take the opening of.</param>
    /// <param name="byteLimit">How many UTF-8 bytes to take at most.</param>
    /// <returns>The longest opening that fits in the limit and still ends on a character boundary.</returns>
    static string Opening(string text, int byteLimit)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length <= byteLimit) return text;

        int cut = byteLimit;
        while (cut > 0 && (utf8[cut] & ContinuationByteMask) == ContinuationByteValue) cut--;
        return Encoding.UTF8.GetString(utf8, 0, cut);
    }

    /// <summary>
    /// Writes out the tool calls counted since the last message, so the gap between two messages shows
    /// what happened in it without carrying any of the payload.
    /// </summary>
    void FlushToolActivity()
    {
        if (pendingToolCalls.Count == 0 && pendingToolResults == 0) return;

        if (options.KeepToolActivity)
        {
            int callCount = pendingToolCalls.Values.Sum();
            string breakdown = string.Join(", ", pendingToolCalls
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} x{pair.Value}"));

            messages.Add(new TranscriptMessage(
                MessageKind.ToolActivity,
                null,
                breakdown.Length > 0 ? $"{callCount} tool calls - {breakdown}" : $"{pendingToolResults} tool results"));
        }

        pendingToolCalls.Clear();
        pendingToolResults = 0;
    }

    /// <summary>
    /// Tells whether this entry has already been read, by its own identifier rather than by its text,
    /// so that two messages which happen to start alike are both kept.
    /// </summary>
    /// <param name="entry">The session entry.</param>
    /// <returns>True when the entry was seen before and should be skipped.</returns>
    bool IsRepeat(JsonElement entry)
    {
        string? entryId = TextOf(entry, "uuid");
        if (entryId == null) return false;
        if (seenEntryIds.Add(entryId)) return false;

        duplicateEntries++;
        return true;
    }

    static JsonElement? ContentOf(JsonElement entry) =>
        entry.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content)
            ? content
            : null;

    static DateTimeOffset? TimestampOf(JsonElement entry) =>
        DateTimeOffset.TryParse(TextOf(entry, "timestamp"), out DateTimeOffset parsed) ? parsed.ToLocalTime() : null;

    static string? TextOf(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static string? Between(string text, string opening, string closing)
    {
        int start = text.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0) return null;
        start += opening.Length;

        int end = text.IndexOf(closing, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    // A partial method with a return type has to state its accessibility, so this one carries the
    // keyword the rest of the file does without.
    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminderBlock();
}
