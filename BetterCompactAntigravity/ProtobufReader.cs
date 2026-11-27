using System.Text;

namespace BetterCompactAntigravity;

/// <summary>
/// Minimal zero-dependency protobuf wire reader for Antigravity step metadata,
/// tool calls, reasoning blocks, and generation metadata.
/// </summary>
static class ProtobufReader
{
    internal readonly record struct StepUsage(long NewInput, long OutputTotal, long CacheHit, long OutputResponse, long OutputThinking)
    {
        public bool HasUsage => NewInput > 0 || CacheHit > 0 || OutputTotal > 0;
    }

    internal readonly record struct StepTimestamps(DateTimeOffset? CreatedAt, DateTimeOffset? CompletedAt);

    internal readonly record struct GenMetadataInfo(string? ModelDisplay, string? ModelSlug, long? ContextLimit);

    /// <summary>Extracts created (f1) and completed (f8/f7/f32) timestamps from a step metadata blob.</summary>
    public static StepTimestamps ExtractTimestamps(ReadOnlySpan<byte> metadata)
    {
        DateTimeOffset? created = null;
        DateTimeOffset? completed = null;

        int pos = 0;
        while (TryReadField(metadata, ref pos, out int field, out int wireType, out ulong varint, out ReadOnlySpan<byte> bytes))
        {
            if (wireType != 2) continue;
            if (field == 1)
            {
                created ??= ParseTimestamp(bytes);
            }
            else if (field is 8 or 7 or 32)
            {
                DateTimeOffset? ts = ParseTimestamp(bytes);
                if (ts != null && (completed == null || ts > completed))
                    completed = ts;
            }
        }

        return new StepTimestamps(created, completed ?? created);
    }

    /// <summary>Extracts per-step token usage from metadata.f9 on planner steps (step_type = 15).</summary>
    public static StepUsage ExtractStepUsage(ReadOnlySpan<byte> metadata)
    {
        int pos = 0;
        while (TryReadField(metadata, ref pos, out int field, out int wireType, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wireType == 2 && field == 9)
                return ParseUsageBlock(bytes);
        }
        return default;
    }

    /// <summary>Extracts the invoked tool name from metadata.f4.f2 on tool steps (step_type = 132).</summary>
    public static string? ExtractToolName(ReadOnlySpan<byte> metadata)
    {
        int pos = 0;
        while (TryReadField(metadata, ref pos, out int field, out int wireType, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wireType != 2 || field != 4) continue;
            int innerPos = 0;
            while (TryReadField(bytes, ref innerPos, out int innerField, out int innerWire, out _, out ReadOnlySpan<byte> innerBytes))
            {
                if (innerWire == 2 && innerField == 2 && innerBytes.Length > 0)
                    return Encoding.UTF8.GetString(innerBytes).Trim();
            }
        }
        return null;
    }

    /// <summary>Extracts the planner reasoning text from step_payload.f20.f3 on step_type = 15.</summary>
    public static string? ExtractReasoningText(ReadOnlySpan<byte> stepPayload)
    {
        int pos = 0;
        while (TryReadField(stepPayload, ref pos, out int field, out int wireType, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wireType != 2 || field != 20) continue;
            int innerPos = 0;
            while (TryReadField(bytes, ref innerPos, out int innerField, out int innerWire, out _, out ReadOnlySpan<byte> innerBytes))
            {
                if (innerWire == 2 && innerField == 3 && innerBytes.Length > 0)
                {
                    string text = Encoding.UTF8.GetString(innerBytes).Trim();
                    if (text.Length > 0) return text;
                }
            }
        }
        return null;
    }

    /// <summary>Extracts model display name, slug, and context window limit from a gen_metadata.data blob.</summary>
    public static GenMetadataInfo ExtractGenMetadata(ReadOnlySpan<byte> data)
    {
        string? display = null;
        string? slug = null;
        long? contextLimit = null;

        int pos = 0;
        while (TryReadField(data, ref pos, out int field, out int wireType, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wireType != 2 || field != 1) continue;
            int innerPos = 0;
            while (TryReadField(bytes, ref innerPos, out int f, out int wt, out _, out ReadOnlySpan<byte> b))
            {
                if (wt != 2) continue;
                if (f == 21 && b.Length > 0)
                {
                    display = Encoding.UTF8.GetString(b).Trim();
                }
                else if (f == 19 && b.Length > 0)
                {
                    slug = Encoding.UTF8.GetString(b).Trim();
                }
                else if (f == 9)
                {
                    long? limit = ExtractContextLimitFromF9(b);
                    if (limit is > 0) contextLimit = limit;
                }
            }
        }

        return new GenMetadataInfo(display, slug, contextLimit);
    }

    static long? ExtractContextLimitFromF9(ReadOnlySpan<byte> f9)
    {
        int pos = 0;
        while (TryReadField(f9, ref pos, out int field, out int wireType, out _, out ReadOnlySpan<byte> bytes))
        {
            if (wireType != 2 || field != 10) continue;
            int innerPos = 0;
            while (TryReadField(bytes, ref innerPos, out int f, out int wt, out ulong v, out _))
            {
                if (wt == 0 && f == 4 && v > 0 && v <= 50_000_000)
                    return (long)v;
            }
        }
        return null;
    }

    static StepUsage ParseUsageBlock(ReadOnlySpan<byte> block)
    {
        long newIn = 0, outTotal = 0, cacheHit = 0, outResp = 0, outThink = 0;
        int pos = 0;
        while (TryReadField(block, ref pos, out int field, out int wireType, out ulong val, out _))
        {
            if (wireType != 0) continue;
            switch (field)
            {
                case 2: newIn = (long)val; break;
                case 3: outTotal = (long)val; break;
                case 5: cacheHit = (long)val; break;
                case 9: outResp = (long)val; break;
                case 10: outThink = (long)val; break;
            }
        }
        return new StepUsage(newIn, outTotal, cacheHit, outResp, outThink);
    }

    static DateTimeOffset? ParseTimestamp(ReadOnlySpan<byte> protoTimestamp)
    {
        long seconds = 0;
        long nanos = 0;
        int pos = 0;
        while (TryReadField(protoTimestamp, ref pos, out int field, out int wireType, out ulong val, out _))
        {
            if (wireType != 0) continue;
            if (field == 1) seconds = (long)val;
            else if (field == 2) nanos = (long)val;
        }

        // Valid Unix seconds range: 2020-01-01 to 2100-01-01.
        if (seconds < 1_577_836_800L || seconds > 4_102_444_800L)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds)
                .AddTicks(Math.Clamp(nanos / 100, 0, TimeSpan.TicksPerSecond - 1))
                .ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    static bool TryReadField(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        out int fieldNumber,
        out int wireType,
        out ulong varintValue,
        out ReadOnlySpan<byte> bytesValue)
    {
        fieldNumber = 0;
        wireType = 0;
        varintValue = 0;
        bytesValue = default;

        if (pos >= buffer.Length) return false;
        if (!TryReadVarint(buffer, ref pos, out ulong tag)) return false;

        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        if (fieldNumber <= 0) return false;

        switch (wireType)
        {
            case 0:
                return TryReadVarint(buffer, ref pos, out varintValue);

            case 1:
                if (pos + 8 > buffer.Length) return false;
                pos += 8;
                return true;

            case 2:
                if (!TryReadVarint(buffer, ref pos, out ulong len) || len > (ulong)(buffer.Length - pos))
                    return false;
                bytesValue = buffer.Slice(pos, (int)len);
                pos += (int)len;
                return true;

            case 5:
                if (pos + 4 > buffer.Length) return false;
                pos += 4;
                return true;

            default:
                return false;
        }
    }

    static bool TryReadVarint(ReadOnlySpan<byte> buffer, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buffer.Length && shift < 64)
        {
            byte b = buffer[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
