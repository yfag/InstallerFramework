using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 3 — Install.
/// Starts the application after files are deployed and registration is complete.
/// On rollback, stops it again (cleanup before file restore).
/// </summary>
public sealed class StartApplicationStep : IDeploymentStep
{
    private readonly IApplicationAdapterFactory _adapterFactory;

    public StartApplicationStep(IApplicationAdapterFactory adapterFactory) => _adapterFactory = adapterFactory;

    public string Name => "Start Application";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        try
        {
            await adapter.StartAsync(context, cancellationToken);
            context.Logger.LogInformation(
                "[{App}@{Server}] Application started.",
                context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Failed to start application: {ex.Message}");
        }
    }

    public async Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);
        try
        {
            await adapter.StopAsync(context, cancellationToken);
            context.Logger.LogInformation(
                "[{App}@{Server}] Rollback: application stopped.",
                context.ApplicationName, context.TargetServer);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex,
                "[{App}@{Server}] Rollback: could not stop application (may already be stopped).",
                context.ApplicationName, context.TargetServer);
        }
    }
}
