using System.Globalization;
using System.Text;

namespace BetterCompactCore;

/// <summary>
/// Builds standardized PascalCase_SnakeSeparated file names: HarnessName_Model_ChatTitle.md
/// </summary>
public static class FileNameBuilder
{
    static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars()];
    const int MaxModelLength = 32;
    const int MaxTitleLength = 64;

    /// <summary>
    /// Builds the base file name without extension in the format HarnessName_Model_ChatTitle.
    /// </summary>
    public static string BuildBaseName(string harnessToken, string modelDisplay, string title, string fallbackId)
    {
        string harnessPart = ToPascalSegment(harnessToken, 24, "Harness");
        string modelPart = ToPascalSegment(modelDisplay, MaxModelLength, "Model");
        string titlePart = ToPascalSegment(title, MaxTitleLength, ToPascalSegment(fallbackId, 16, "Sohbet"));
        return $"{harnessPart}_{modelPart}_{titlePart}";
    }

    /// <summary>
    /// Converts arbitrary text into a filename-safe PascalCase token preserving Turkish letters.
    /// </summary>
    public static string ToPascalSegment(string? input, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;

        StringBuilder result = new();
        bool capitalizeNext = true;

        foreach (char ch in input)
        {
            if (char.IsLetterOrDigit(ch) && !InvalidChars.Contains(ch))
            {
                char written = capitalizeNext ? char.ToUpper(ch, TrCulture) : ch;
                result.Append(written);
                capitalizeNext = false;
                if (result.Length >= maxLength) break;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        string cleaned = result.ToString().Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
