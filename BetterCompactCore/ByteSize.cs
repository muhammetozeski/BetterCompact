using System.Globalization;

namespace BetterCompactCore;

/// <summary>Formats byte counts into concise human-readable sizes.</summary>
public static class ByteSize
{
    static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    const double Kibibyte = 1024d;
    const double Mebibyte = 1024d * 1024d;
    const double Gibibyte = 1024d * 1024d * 1024d;

    /// <summary>Formats <paramref name="bytes"/> using B, KB, MB, or GB with Turkish decimal formatting.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= (long)Gibibyte => $"{(bytes / Gibibyte).ToString("0.00", TrCulture)} GB",
        >= (long)Mebibyte => $"{(bytes / Mebibyte).ToString("0.0", TrCulture)} MB",
        >= (long)Kibibyte => $"{(bytes / Kibibyte).ToString("0.0", TrCulture)} KB",
        _ => $"{bytes} B"
    };

    /// <summary>Formats an integer token count with dot thousand separators.</summary>
    public static string FormatNumber(long value) => value.ToString("N0", TrCulture);

    /// <summary>Formats a percentage value with one decimal digit.</summary>
    public static string FormatPercent(double ratio) => $"%{ratio.ToString("0.0", TrCulture)}";
}
