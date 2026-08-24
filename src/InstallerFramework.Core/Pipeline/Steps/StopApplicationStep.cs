using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 2 — Snapshot.
/// Gracefully stops the running application before backup and file operations.
/// Records whether it was running so rollback knows whether to restart.
/// </summary>
public sealed class StopApplicationStep : IDeploymentStep
{
    private readonly IApplicationAdapterFactory _adapterFactory;

    public StopApplicationStep(IApplicationAdapterFactory adapterFactory) => _adapterFactory = adapterFactory;

    public string Name => "Stop Application";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        // Skip if not yet installed (fresh install)
        if (context.IsFreshInstall)
        {
            context.Logger.LogDebug("[{App}@{Server}] Fresh install — nothing to stop.", context.ApplicationName, context.TargetServer);
            context.ApplicationWasRunning = false;
            return StepResult.Ok();
        }

        try
        {
            context.ApplicationWasRunning = await adapter.IsRunningAsync(context, cancellationToken);

            if (!context.ApplicationWasRunning)
            {
                context.Logger.LogInformation(
                    "[{App}@{Server}] Application is not running — no stop needed.",
                    context.ApplicationName, context.TargetServer);
                return StepResult.Ok();
            }

            await adapter.StopAsync(context, cancellationToken);

            context.Logger.LogInformation(
                "[{App}@{Server}] Application stopped.",
                context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Failed to stop application: {ex.Message}");
        }
    }

    public async Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (!context.ApplicationWasRunning) return;

        try
        {
            var adapter = _adapterFactory.Create(context);
            await adapter.StartAsync(context, cancellationToken);
            context.Logger.LogInformation("[{App}@{Server}] Rollback: application restarted.", context.ApplicationName, context.TargetServer);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "[{App}@{Server}] Rollback: failed to restart application.", context.ApplicationName, context.TargetServer);
        }
    }
}
