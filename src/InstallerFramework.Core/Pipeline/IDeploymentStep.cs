using InstallerFramework.Core.Models;

namespace InstallerFramework.Core.Pipeline;

/// <summary>
/// A single step in the deployment pipeline.
/// Each step must be independently reversible — if a later step fails, earlier steps
/// are rolled back in reverse order.
/// </summary>
public interface IDeploymentStep
{
    /// <summary>Human-readable name used in logging and error messages.</summary>
    string Name { get; }

    /// <summary>
    /// Execute this step. On failure, return StepResult.Fail(reason).
    /// Do NOT throw — catch internal exceptions and convert to StepResult.Fail.
    /// </summary>
    Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undo the effect of a successful ExecuteAsync call.
    /// Called only if this step previously succeeded and a later step failed.
    /// Best-effort: log but do not throw if rollback itself fails.
    /// </summary>
    Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default);
}

public readonly record struct StepResult(bool Success, string? ErrorMessage = null)
{
    public static StepResult Ok() => new(true);
    public static StepResult Fail(string error) => new(false, error);
    public static StepResult Fail(Exception ex) => new(false, ex.Message);
}
