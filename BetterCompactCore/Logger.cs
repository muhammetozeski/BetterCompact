using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace BetterCompactCore;

/// <summary>
/// Writes structured diagnostic logs to a rotating folder under %TEMP%\BetterCompact\&lt;ProgramName&gt;\Logs.
/// </summary>
public static class Logger
{
    internal const bool IsLogEnabled = true;
    private static int _activateLogging = 1;

    /// <summary>Gets or sets whether diagnostic logging to disk is active.</summary>
    public static bool ActivateLogging
    {
        get => Interlocked.CompareExchange(ref _activateLogging, 0, 0) == 1;
        set => Interlocked.Exchange(ref _activateLogging, value ? 1 : 0);
    }

    public static readonly string StartTime = DateTime.Now.ToString("yyyy.MM.dd HH.mm.ss.ff");
    public static readonly string LogsFolder = AppPaths.LogsFolder;
    public const string LogFileNamePrefix = "Logs";
    public static readonly string LogFileName = Path.Combine(LogsFolder, $"{LogFileNamePrefix} {StartTime}.txt");

    const int DeleteOlderThanLastXFile = 5;

    public static readonly ConcurrentQueue<string> AllLogs = new();
    public static readonly ConcurrentQueue<string?> AllLogsUserFriendly = new();

    private static readonly BlockingCollection<string> _logQueue = [];
    private static readonly ManualResetEventSlim _writerDone = new(false);
    private static int _flushed;

    static Logger()
    {
        if (!ActivateLogging)
        {
            _writerDone.Set();
            return;
        }

        try
        {
            Directory.CreateDirectory(LogsFolder);
            DeleteOldestFiles(LogsFolder, DeleteOlderThanLastXFile, LogFileNamePrefix);
        }
        catch
        {
            // Logging setup must never crash the CLI tool.
        }

        new Thread(() =>
        {
            try
            {
                foreach (string message in _logQueue.GetConsumingEnumerable())
                {
                    try { File.AppendAllText(LogFileName, message + "\n"); }
                    catch { /* Swallow transient temp write errors. */ }
                }
            }
            finally
            {
                _writerDone.Set();
            }
        })
        {
            IsBackground = true,
            Name = "BetterCompactLogger"
        }.Start();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>Completes the queue and briefly waits for pending log entries to flush before process exit.</summary>
    public static void Flush()
    {
        if (Interlocked.Exchange(ref _flushed, 1) != 0) return;
        try
        {
            _logQueue.CompleteAdding();
            _writerDone.Wait(TimeSpan.FromMilliseconds(350));
        }
        catch { /* Ignore shutdown race. */ }
    }

    /// <summary>Records a diagnostic entry and optionally prints it to the console.</summary>
    public static string? Log(
        object? MessageObject,
        ConsoleColor? consoleColor = null,
        bool PrintToConsole = false,
        bool WriteToDisk = true,
        bool UseNewLine = true,
        [CallerMemberName] string callerFunction = "",
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLine = 0,
        bool isRun = true)
    {
        if (!isRun || (!PrintToConsole && !WriteToDisk)) return null;

        if (!ActivateLogging)
        {
            if (PrintToConsole) WriteConsole(MessageObject, consoleColor, UseNewLine);
            return null;
        }

        string callerFile = string.IsNullOrWhiteSpace(callerFilePath) ? "null" : Path.GetFileName(callerFilePath);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string header = $"> [{now}] [{callerFile}/{callerFunction} Line: {callerLine} Thread Id: {Environment.CurrentManagedThreadId}]:\n";
        string? returnValue = null;
        string body;

        if (MessageObject is System.Collections.IEnumerable enumerable and not string)
        {
            List<string> lines = [];
            foreach (object? item in enumerable)
                lines.Add(item?.ToString() ?? "null");
            body = string.Join("\n", lines);
            returnValue = body;
        }
        else
        {
            returnValue = MessageObject?.ToString();
            body = returnValue ?? "null";
        }

        string entry = $"{header}{body}\n-----------------------------\n";

        if (PrintToConsole) WriteConsole(MessageObject, consoleColor, UseNewLine);

        AllLogs.Enqueue(entry);
        AllLogsUserFriendly.Enqueue(returnValue);

        if (WriteToDisk && !_logQueue.IsAddingCompleted)
        {
            try { _logQueue.Add(entry); }
            catch (InvalidOperationException) { /* Queue closed during process exit. */ }
        }

        return returnValue;
    }

    static void WriteConsole(object? messageObject, ConsoleColor? consoleColor, bool useNewLine)
    {
        ConsoleColor previous = Console.ForegroundColor;
        if (consoleColor != null) Console.ForegroundColor = consoleColor.Value;
        if (useNewLine) Console.WriteLine(messageObject ?? "null");
        else Console.Write(messageObject ?? "null");
        Console.ForegroundColor = previous;
    }
}
