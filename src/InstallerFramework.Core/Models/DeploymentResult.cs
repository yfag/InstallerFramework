namespace InstallerFramework.Core.Models;

public sealed class DeploymentResult
{
    public bool Success { get; private init; }
    public bool Skipped { get; private init; }
    public string ApplicationName { get; private init; } = string.Empty;
    public string TargetServer { get; private init; } = string.Empty;
    public string? InstalledVersion { get; private init; }
    public string? PreviousVersion { get; private init; }
    public string? FailedStep { get; private init; }
    public string? ErrorMessage { get; private init; }
    public bool RolledBack { get; private init; }
    public bool RollbackFailed { get; private init; }
    public bool RollbackSkipped { get; private init; }
    public TimeSpan Duration { get; private init; }

    public static DeploymentResult Succeeded(
        string application, string server, string version, string? previousVersion, TimeSpan duration) =>
        new()
        {
            Success = true,
            ApplicationName = application,
            TargetServer = server,
            InstalledVersion = version,
            PreviousVersion = previousVersion,
            Duration = duration
        };

    /// <summary>
    /// Already at the correct version — no action taken.
    /// </summary>
    public static DeploymentResult AlreadyUpToDate(
        string application, string server, string version) =>
        new()
        {
            Success = true,
            Skipped = true,
            ApplicationName = application,
            TargetServer = server,
            InstalledVersion = version
        };

    /// <summary>
    /// Application is not installed on this server — uninstall is a no-op.
    /// </summary>
    public static DeploymentResult NotInstalled(string application, string server) =>
        new()
        {
            Success = true,
            Skipped = true,
            ApplicationName = application,
            TargetServer = server,
            InstalledVersion = null
        };

    public static DeploymentResult Failed(
        string application, string server, string failedStep, string error,
        bool rolledBack, bool rollbackFailed, bool rollbackSkipped, TimeSpan duration) =>
        new()
        {
            Success = false,
            ApplicationName = application,
            TargetServer = server,
            FailedStep = failedStep,
            ErrorMessage = error,
            RolledBack = rolledBack,
            RollbackFailed = rollbackFailed,
            RollbackSkipped = rollbackSkipped,
            Duration = duration
        };

    public override string ToString() =>
        Skipped
            ? $"[SKIP] {ApplicationName} v{InstalledVersion} already installed on {TargetServer}"
            : Success
                ? $"[OK]   {ApplicationName} v{InstalledVersion} → {TargetServer} ({Duration.TotalSeconds:F1}s)"
                : $"[FAIL] {ApplicationName} → {TargetServer} at step '{FailedStep}': {ErrorMessage}" +
                  (RolledBack ? " [ROLLED BACK]" : RollbackFailed ? " [ROLLBACK FAILED]" : RollbackSkipped ? " [ROLLBACK SKIPPED]" : string.Empty);
}

/// <summary>Aggregated result for a full environment deployment run.</summary>
public sealed class EnvironmentDeploymentSummary
{
    public string EnvironmentName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public List<DeploymentResult> Results { get; init; } = [];

    public int TotalCount    => Results.Count;
    public int SuccessCount  => Results.Count(r => r.Success && !r.Skipped);
    public int SkippedCount  => Results.Count(r => r.Skipped);
    public int FailureCount  => Results.Count(r => !r.Success);
    public bool AllSucceeded => Results.All(r => r.Success); // Skipped counts as success
    public TimeSpan TotalDuration => CompletedAt - StartedAt;
}
