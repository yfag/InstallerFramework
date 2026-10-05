using Serilog;

namespace InstallerFramework.Cli;

/// <summary>
/// Owns the location of the run log files and prunes old ones.
/// One timestamped file is written per run (<c>installer_yyyyMMdd_HHmmss.log</c>) in a
/// <c>log</c> folder next to the exe; the file name sorts chronologically.
/// </summary>
internal static class LogRetention
{
    public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "log");

    private const string FilePattern = "installer_*.log";

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> log files and deletes the rest.
    /// The current run's log is the newest file, so it is always kept.
    /// A value below 1 disables cleanup. Failures (e.g. a file locked by a concurrent run)
    /// are logged and never interrupt the command.
    /// </summary>
    public static void Prune(int keep)
    {
        if (keep < 1) return;

        try
        {
            var stale = new DirectoryInfo(LogDirectory)
                .EnumerateFiles(FilePattern)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(keep);

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                    Log.Debug("Deleted old log file {File} (log-retention-files: {Keep})", file.Name, keep);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug("Could not delete old log file {File}: {Reason}", file.Name, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Log cleanup skipped: {Reason}", ex.Message);
        }
    }
}
