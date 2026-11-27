using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BetterCompactCore;

/// <summary>Describes the outcome without treating a failed operation as successful.</summary>
public enum OperationStatus
{
    Failed,
    Succeeded,
    Canceled
}

/// <summary>Contains the value or the failure reported by the final exception boundary.</summary>
/// <typeparam name="T">The successful result type.</typeparam>
public readonly record struct OperationResult<T>(OperationStatus Status, T? Value, Exception? Error)
{
    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool Succeeded => Status == OperationStatus.Succeeded;
}

/// <summary>
/// Owns final exception containment after the operation has exhausted its recovery paths.
/// </summary>
public static class ExceptionBoundary
{
    const bool IsLogEnabled = true;
    static readonly object EmergencyLogLock = new();

    /// <summary>Runs an operation and logs and contains its final failure.</summary>
    public static OperationResult<T> RunSafely<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string operationName = "")
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string operationId = Guid.NewGuid().ToString("N");
        OperationStatus status = OperationStatus.Failed;
        LogSafely($"operation={operationName}; id={operationId}; state=started", isRun: IsLogEnabled);

        try
        {
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            T value = operation();
            status = OperationStatus.Succeeded;
            return new(OperationStatus.Succeeded, value, null);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            status = OperationStatus.Canceled;
            LogSafely($"operation={operationName}; id={operationId}; state=canceled; error={exception}", isRun: IsLogEnabled);
            return new(OperationStatus.Canceled, default, exception);
        }
        catch (Exception exception)
        {
            LogSafely($"operation={operationName}; id={operationId}; state=failed; error={exception}", isRun: IsLogEnabled);
            return new(OperationStatus.Failed, default, exception);
        }
        finally
        {
            stopwatch.Stop();
            LogSafely($"operation={operationName}; id={operationId}; state=finished; outcome={status}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}", isRun: IsLogEnabled);
        }
    }

    /// <summary>Runs an action with the same final failure boundary as value-returning operations.</summary>
    public static OperationResult<bool> RunSafely(
        Action operation,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string operationName = "") =>
        RunSafely(() =>
        {
            ArgumentNullException.ThrowIfNull(operation);
            operation();
            return true;
        }, cancellationToken, operationName);

    /// <summary>Prevents logging failures from escaping the final application boundary.</summary>
    public static void LogSafely(string message, bool isRun = true)
    {
        if (!isRun) return;
        try
        {
            Log(message, isRun: isRun);
        }
        catch (Exception loggingException)
        {
            string emergencyMessage = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}Logging failure: {loggingException}{Environment.NewLine}";
            try
            {
                lock (EmergencyLogLock)
                {
                    Directory.CreateDirectory(AppPaths.AppCache);
                    File.AppendAllText(Path.Combine(AppPaths.AppCache, "ExceptionBoundary.Emergency.log"), emergencyMessage);
                }
            }
            catch
            {
                try { Console.Error.WriteLine(emergencyMessage); }
                catch { /* All logging sinks failed; contain the original failure. */ }
            }
        }
    }
}
