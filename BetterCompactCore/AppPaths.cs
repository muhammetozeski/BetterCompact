using System.Reflection;

namespace BetterCompactCore;

/// <summary>
/// Resolves runtime paths so BetterCompact tools keep their executable directory clean
/// and write diagnostic logs exclusively under a dedicated %TEMP% subfolder.
/// </summary>
public static class AppPaths
{
    public static readonly string ProgramName =
        Assembly.GetEntryAssembly()?.GetName().Name ?? "BetterCompact";

    public static readonly string PayloadDirectory =
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static readonly string AppCache =
        Path.Combine(Path.GetTempPath(), "BetterCompact", ProgramName);

    public static readonly string LogsFolder =
        Path.Combine(AppCache, "Logs");

    public static readonly string PayloadVersion =
        (Assembly.GetEntryAssembly() ?? typeof(AppPaths).Assembly)
            .GetName().Version?.ToString() ?? "2.0.0.0";
}
