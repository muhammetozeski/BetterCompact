using System.Text;

namespace BetterCompactCore;

/// <summary>
/// Holds command-line options shared by all BetterCompact executables.
/// </summary>
public sealed record CommonOptions(
    string Input,
    string OutputDirectory,
    bool IncludeToc,
    bool KeepReasoning,
    bool KeepInjected,
    bool KeepCompactSummaries,
    bool KeepSystemReminders,
    bool KeepToolActivity,
    int MaximumMessageBytes,
    int OpeningBytes,
    bool SplitOutput,
    int MaxPartBytes,
    int MaxPartLines,
    bool ListMode,
    int ListLimit,
    string FindQuery)
{
    public const int DefaultMaxMessageBytes = 65536;
    public const int DefaultOpeningBytes = 512;
    public const int AntigravityMaxPartBytes = 44 * 1024;
    public const int AntigravityMaxPartLines = 700;
    public const int DefaultListLimit = 15;
}

/// <summary>
/// Parses standardized BetterCompact command-line flags across all harness executables.
/// </summary>
public static class CommonCommandLine
{
    /// <summary>Parses CLI arguments using harness-specific splitting defaults.</summary>
    public static bool TryParse(
        string[] arguments,
        bool defaultSplitOutput,
        out CommonOptions? options,
        out string error,
        out bool showHelp)
    {
        options = null;
        error = string.Empty;
        showHelp = false;

        string input = string.Empty;
        string output = string.Empty;
        bool includeToc = false;
        bool keepReasoning = false;
        bool keepInjected = false;
        bool keepSummaries = false;
        bool keepSystemReminders = false;
        bool keepTools = true;
        int maxMessageBytes = CommonOptions.DefaultMaxMessageBytes;
        int openingBytes = CommonOptions.DefaultOpeningBytes;
        bool splitOutput = defaultSplitOutput;
        int maxPartBytes = CommonOptions.AntigravityMaxPartBytes;
        int maxPartLines = CommonOptions.AntigravityMaxPartLines;
        bool listMode = false;
        int listLimit = CommonOptions.DefaultListLimit;
        string findQuery = string.Empty;

        for (int i = 0; i < arguments.Length; i++)
        {
            string arg = arguments[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                case "/?":
                    showHelp = true;
                    return false;

                case "-o":
                case "--output":
                    if (!TryTakeNext(arguments, ref i, out output))
                    {
                        error = "--output için bir klasör veya dosya yolu verilmeli.";
                        return false;
                    }
                    break;

                case "--Contents":
                case "--contents":
                case "--icindekiler":
                case "--fihrist":
                    includeToc = true;
                    break;

                case "--reasoning":
                case "--thinking":
                case "--dusunce":
                    keepReasoning = true;
                    break;

                case "--injected":
                    keepInjected = true;
                    break;

                case "--summaries":
                    keepSummaries = true;
                    break;

                case "--system-reminders":
                    keepSystemReminders = true;
                    break;

                case "--everything":
                    keepReasoning = true;
                    keepInjected = true;
                    keepSummaries = true;
                    keepSystemReminders = true;
                    break;

                case "--no-tools":
                    keepTools = false;
                    break;

                case "--split":
                    splitOutput = true;
                    break;

                case "--max-part-kb":
                    if (!TryTakeInt(arguments, ref i, min: 4, out int kb))
                    {
                        error = "--max-part-kb için en az 4 olan bir tam sayı verilmeli.";
                        return false;
                    }
                    maxPartBytes = kb * 1024;
                    splitOutput = true;
                    break;

                case "--max-part-lines":
                    if (!TryTakeInt(arguments, ref i, min: 50, out maxPartLines))
                    {
                        error = "--max-part-lines için en az 50 olan bir tam sayı verilmeli.";
                        return false;
                    }
                    splitOutput = true;
                    break;

                case "--max-message":
                    if (!TryTakeInt(arguments, ref i, min: 0, out maxMessageBytes))
                    {
                        error = "--max-message için 0 veya pozitif bayt değeri verilmeli.";
                        return false;
                    }
                    break;

                case "--keep-opening":
                    if (!TryTakeInt(arguments, ref i, min: 0, out openingBytes))
                    {
                        error = "--keep-opening için 0 veya pozitif bayt değeri verilmeli.";
                        return false;
                    }
                    break;

                case "--list":
                    listMode = true;
                    if (i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out int parsedLimit) && parsedLimit > 0)
                    {
                        listLimit = parsedLimit;
                        i++;
                    }
                    break;

                case "--find":
                case "--search-title":
                    if (!TryTakeNext(arguments, ref i, out findQuery))
                    {
                        error = "--find için aranacak kelime verilmeli.";
                        return false;
                    }
                    listMode = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Bilinmeyen parametre: {arg}";
                        return false;
                    }
                    if (input.Length > 0)
                    {
                        error = $"Beklenmeyen ek parametre: {arg}";
                        return false;
                    }
                    input = arg;
                    break;
            }
        }

        if (!listMode && input.Length == 0)
        {
            error = "Sohbet UUID'si, dosya yolu, --list veya --find verilmedi.";
            return false;
        }

        options = new CommonOptions(
            input,
            output,
            includeToc,
            keepReasoning,
            keepInjected,
            keepSummaries,
            keepSystemReminders,
            keepTools,
            maxMessageBytes,
            openingBytes,
            splitOutput,
            maxPartBytes,
            maxPartLines,
            listMode,
            listLimit,
            findQuery);
        return true;
    }

    /// <summary>Prints concise usage instructions for a harness executable.</summary>
    public static void PrintHelp(string exeName, string harnessName, string inputHint, bool splitsByDefault)
    {
        StringBuilder sb = new();
        sb.AppendLine($"{exeName} ({harnessName})");
        sb.AppendLine();
        sb.AppendLine("Kullanım:");
        sb.AppendLine($"  {exeName} <UUID | dosya-yolu> [seçenekler]");
        sb.AppendLine($"  {exeName} --list [adet]");
        sb.AppendLine($"  {exeName} --find <kelime>");
        sb.AppendLine();
        sb.AppendLine($"Girdi: {inputHint}");
        sb.AppendLine();
        sb.AppendLine("Seçenekler:");
        sb.AppendLine("  -o, --output <klasör|dosya>   Çıktı hedefi (varsayılan: NewTemp klasörü)");
        sb.AppendLine("  --Contents                    Başa İçindekiler şablonu ekler, mesajları k1/a1 numaralar");
        sb.AppendLine("  --reasoning, --thinking       Düşünme/akıl yürütme bloklarını katlanabilir ekler");
        sb.AppendLine("  --injected                    Sistem/enjekte blokları dahil eder");
        sb.AppendLine("  --summaries                   Eski sıkıştırma özetlerini dahil eder");
        sb.AppendLine("  --everything                  Tüm isteğe bağlı blokları dahil eder");
        sb.AppendLine("  --no-tools                    Araç çağrısı özet satırlarını gizler");
        sb.AppendLine("  --max-message <bayt>          Mesaj boyut sınırı (varsayılan: 65536, 0 = sınırsız)");
        sb.AppendLine("  --keep-opening <bayt>         Kırpılan mesajdan korunacak başlangıç (varsayılan: 512)");
        sb.AppendLine("  --split                       Çıktıyı 44 KB / 700 satır parçalara böler");
        Console.Write(sb.ToString());
    }

    static bool TryTakeNext(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }
        value = args[++index];
        return true;
    }

    static bool TryTakeInt(string[] args, ref int index, int min, out int value)
    {
        value = 0;
        if (index + 1 >= args.Length) return false;
        if (!int.TryParse(args[index + 1], out value) || value < min) return false;
        index++;
        return true;
    }
}
