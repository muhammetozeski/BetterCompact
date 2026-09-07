namespace BetterCompact;

/// <summary>
/// Which part of the conversation a message came from. The reader assigns exactly one of these to
/// every piece of text it keeps, and the composer decides from it whether to write the text out.
/// </summary>
enum MessageKind
{
    /// <summary>Text the person typed into the prompt.</summary>
    User,

    /// <summary>A slash command the person ran, flattened to its name and arguments.</summary>
    SlashCommand,

    /// <summary>Text the assistant wrote back.</summary>
    Assistant,

    /// <summary>The assistant's reasoning block, which the transcript stores separately from its reply.</summary>
    Thinking,

    /// <summary>Text that arrived in the user turn but nobody typed: task notifications, command echoes, reminders.</summary>
    Injected,

    /// <summary>A summary written when the session ran out of context and restated the conversation so far.</summary>
    CompactSummary,

    /// <summary>The count of tool calls and tool results that happened between the two messages around it.</summary>
    ToolActivity
}

/// <summary>
/// One kept piece of the conversation. <paramref name="Text"/> is stored exactly as it appeared in the
/// session file and is never shortened.
/// </summary>
/// <param name="Kind">Which part of the conversation it came from.</param>
/// <param name="Timestamp">When the session file recorded it, or null when the entry carried no time.</param>
/// <param name="Text">The message itself.</param>
sealed record TranscriptMessage(MessageKind Kind, DateTimeOffset? Timestamp, string Text);

/// <summary>
/// How much of the session file ended up where. Every line the reader saw is counted in exactly one of
/// these, so a shrinking output can always be explained.
/// </summary>
/// <param name="TotalLines">Lines read from the file, blank lines excluded.</param>
/// <param name="UnreadableLines">Lines that were not valid JSON.</param>
/// <param name="DuplicateEntries">Entries whose uuid had already been seen.</param>
/// <param name="UserMessages">Messages the person typed.</param>
/// <param name="SlashCommands">Slash commands the person ran.</param>
/// <param name="AssistantMessages">Replies the assistant wrote.</param>
/// <param name="ThinkingBlocks">Assistant reasoning blocks.</param>
/// <param name="InjectedMessages">Text injected into the user turn by the tool itself.</param>
/// <param name="CompactSummaries">Context-limit summaries found in the file.</param>
/// <param name="ToolCalls">Tool invocations the assistant made.</param>
/// <param name="ToolResults">Tool outputs that came back.</param>
/// <param name="Attachments">Attachment entries, which hold no conversation text.</param>
/// <param name="OversizedMessages">Messages cut down to their opening because they were over the limit the caller set.</param>
sealed record TranscriptStatistics(
    int TotalLines,
    int UnreadableLines,
    int DuplicateEntries,
    int UserMessages,
    int SlashCommands,
    int AssistantMessages,
    int ThinkingBlocks,
    int InjectedMessages,
    int CompactSummaries,
    int ToolCalls,
    int ToolResults,
    int Attachments,
    int OversizedMessages);

/// <summary>
/// A whole session file after reading: what it was called, what it contained, and what was in it.
/// </summary>
/// <param name="Title">The title the session carried, or the file name when it had none.</param>
/// <param name="SessionId">The session's own identifier.</param>
/// <param name="Messages">Everything worth keeping, in the order it happened.</param>
/// <param name="Statistics">The per-kind counts behind those messages.</param>
sealed record TranscriptDocument(
    string Title,
    string SessionId,
    IReadOnlyList<TranscriptMessage> Messages,
    TranscriptStatistics Statistics);
