using System.Globalization;
using System.Text;

namespace BetterCompact;

/// <summary>
/// Lays a read session out as Markdown. Message text goes through untouched; this class only decides
/// what heading stands above it and what the header block says.
/// </summary>
static class MarkdownComposer
{
    const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    const string DateOnlyFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// Builds the whole document.
    /// </summary>
    /// <param name="document">The session as it was read.</param>
    /// <returns>The Markdown text, ready to be written to disk.</returns>
    public static string Compose(TranscriptDocument document)
    {
        StringBuilder builder = new();

        builder.AppendLine($"# {document.Title}");
        builder.AppendLine();
        AppendHeader(builder, document);
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (TranscriptMessage message in document.Messages)
        {
            if (message.Kind == MessageKind.ToolActivity)
            {
                builder.AppendLine($"> {message.Text}");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine(HeadingFor(message));
            builder.AppendLine();
            builder.AppendLine(message.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    static void AppendHeader(StringBuilder builder, TranscriptDocument document)
    {
        if (document.SessionId.Length > 0) builder.AppendLine($"> Session `{document.SessionId}`  ");

        DateTimeOffset? first = document.Messages.FirstOrDefault(message => message.Timestamp != null)?.Timestamp;
        DateTimeOffset? last = document.Messages.LastOrDefault(message => message.Timestamp != null)?.Timestamp;
        if (first != null && last != null)
            builder.AppendLine($"> {Format(first, DateOnlyFormat)} - {Format(last, DateOnlyFormat)}  ");

        TranscriptStatistics statistics = document.Statistics;
        builder.AppendLine(
            $"> {statistics.UserMessages} user messages, {statistics.SlashCommands} slash commands, " +
            $"{statistics.AssistantMessages} replies, kept from {statistics.TotalLines} entries  ");
        builder.AppendLine(
            $"> Dropped: {statistics.ToolCalls} tool calls, {statistics.ToolResults} tool results, " +
            $"{statistics.InjectedMessages} injected, {statistics.ThinkingBlocks} thinking, " +
            $"{statistics.CompactSummaries} compact summaries");
    }

    static string HeadingFor(TranscriptMessage message)
    {
        string label = message.Kind switch
        {
            MessageKind.User => "## User",
            MessageKind.SlashCommand => "## Command",
            MessageKind.Assistant => "## Claude",
            MessageKind.Thinking => "### Thinking",
            MessageKind.Injected => "## Injected",
            MessageKind.CompactSummary => "## Compact summary",
            _ => "## Message"
        };

        return message.Timestamp == null ? label : $"{label} - {Format(message.Timestamp, TimestampFormat)}";
    }

    static string Format(DateTimeOffset? moment, string format) =>
        moment?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;
}
