using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline;

/// <summary>
/// Runs the ordered deployment pipeline for a single application on a single server.
/// Maintains a stack of completed steps; on failure, rolls back in reverse order.
/// </summary>
public sealed class DeploymentOrchestrator
{
    private readonly IReadOnlyList<IDeploymentStep> _steps;
    private readonly ILogger<DeploymentOrchestrator> _logger;

    public DeploymentOrchestrator(
        IEnumerable<IDeploymentStep> steps,
        ILogger<DeploymentOrchestrator> logger)
    {
        _steps = steps.ToList().AsReadOnly();
        _logger = logger;
    }

    public async Task<DeploymentResult> ExecuteAsync(
        DeploymentContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var completedSteps = new Stack<IDeploymentStep>();

        _logger.LogInformation(
            "=== Starting deployment: {App} v{Version} → {Server} ({Steps} steps) ===",
            context.ApplicationName, context.TargetVersion, context.TargetServer, _steps.Count);

        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "[{App}@{Server}] Step: {Step}",
                context.ApplicationName, context.TargetServer, step.Name);

            StepResult result;
            try
            {
                result = await step.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{App}@{Server}] Step '{Step}' threw an unhandled exception",
                    context.ApplicationName, context.TargetServer, step.Name);
                result = StepResult.Fail(ex);
            }

            if (!result.Success)
            {
                _logger.LogError(
                    "[{App}@{Server}] Step '{Step}' FAILED: {Error}",
                    context.ApplicationName, context.TargetServer, step.Name, result.ErrorMessage);

                bool rolledBack, rollbackFailed, rollbackSkipped;

                if (context.NoRollback)
                {
                    _logger.LogWarning(
                        "[{App}@{Server}] Rollback is disabled (--no-rollback). " +
                        "Server is left in its current state — inspect and remediate before retrying.",
                        context.ApplicationName, context.TargetServer);
                    rolledBack = false;
                    rollbackFailed = false;
                    rollbackSkipped = true;
                }
                else
                {
                    (rolledBack, rollbackFailed) = await RollbackAsync(completedSteps, context, cancellationToken);
                    rollbackSkipped = false;
                }

                sw.Stop();
                return DeploymentResult.Failed(
                    context.ApplicationName,
                    context.TargetServer,
                    step.Name,
                    result.ErrorMessage ?? "Unknown error",
                    rolledBack,
                    rollbackFailed,
                    rollbackSkipped,
                    sw.Elapsed);
            }

            completedSteps.Push(step);
            _logger.LogInformation(
                "[{App}@{Server}] Step '{Step}' completed.",
                context.ApplicationName, context.TargetServer, step.Name);
        }

        sw.Stop();
        _logger.LogInformation(
            "=== Deployment succeeded: {App} v{Version} → {Server} in {Duration:F1}s ===",
            context.ApplicationName, context.TargetVersion, context.TargetServer, sw.Elapsed.TotalSeconds);

        return DeploymentResult.Succeeded(
            context.ApplicationName,
            context.TargetServer,
            context.TargetVersion,
            context.PreviousVersion,
            sw.Elapsed);
    }

    private async Task<(bool RolledBack, bool RollbackFailed)> RollbackAsync(
        Stack<IDeploymentStep> completedSteps,
        DeploymentContext context,
        CancellationToken cancellationToken)
    {
        if (completedSteps.Count == 0)
            return (false, false);

        _logger.LogWarning(
            "[{App}@{Server}] Beginning rollback — reverting {Count} completed step(s).",
            context.ApplicationName, context.TargetServer, completedSteps.Count);

        var anyRollbackFailed = false;

        while (completedSteps.TryPop(out var step))
        {
            try
            {
                await step.RollbackAsync(context, cancellationToken);
                _logger.LogInformation(
                    "[{App}@{Server}] Rolled back: {Step}",
                    context.ApplicationName, context.TargetServer, step.Name);
            }
            catch (Exception ex)
            {
                anyRollbackFailed = true;
                _logger.LogError(ex,
                    "[{App}@{Server}] ROLLBACK FAILED for step '{Step}' — MANUAL INTERVENTION MAY BE REQUIRED",
                    context.ApplicationName, context.TargetServer, step.Name);
            }
        }

        if (anyRollbackFailed)
            _logger.LogCritical(
                "[{App}@{Server}] One or more rollback steps failed. The server may be in an inconsistent state.",
                context.ApplicationName, context.TargetServer);
        else
            _logger.LogWarning(
                "[{App}@{Server}] Rollback completed — previous version restored.",
                context.ApplicationName, context.TargetServer);

        return (true, anyRollbackFailed);
    }
}
