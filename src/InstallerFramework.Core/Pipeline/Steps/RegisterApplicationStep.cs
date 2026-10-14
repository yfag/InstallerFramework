using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 3 — Install.
/// Registers (or updates) the application with the OS:
///   Windows Service: sc.exe create/config
///   IIS:             create/update app pool and web application
/// Handles both fresh installs and updates idempotently.
/// On rollback, removes the registration (fresh install) or restores previous config (update).
/// </summary>
public sealed class RegisterApplicationStep : IDeploymentStep
{
    private readonly IApplicationAdapterFactory _adapterFactory;

    public RegisterApplicationStep(IApplicationAdapterFactory adapterFactory) => _adapterFactory = adapterFactory;

    public string Name => "Register Application";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        try
        {
            await adapter.RegisterAsync(context, cancellationToken);
            context.Logger.LogInformation(
                "[{App}@{Server}] Application registered/updated.",
                context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            // The orchestrator only rolls back steps that completed, so this step's own partial
            // work (e.g. an app pool and web application created before a later error) would be
            // left on the server. On a fresh install nothing existed before, so remove it now.
            if (context.IsFreshInstall && !context.NoRollback)
                await CleanUpPartialRegistrationAsync(adapter, context, cancellationToken);

            return StepResult.Fail($"Application registration failed: {ex.Message}");
        }
    }

    private static async Task CleanUpPartialRegistrationAsync(
        IApplicationAdapter adapter, DeploymentContext context, CancellationToken cancellationToken)
    {
        try
        {
            await adapter.UnregisterAsync(context, cancellationToken);
            context.Logger.LogWarning(
                "[{App}@{Server}] Registration failed — removed the partial registration.",
                context.ApplicationName, context.TargetServer);
        }
        catch (Exception cleanupEx)
        {
            context.Logger.LogError(cleanupEx,
                "[{App}@{Server}] Registration failed and the partial registration could not be removed — remove it manually.",
                context.ApplicationName, context.TargetServer);
        }
    }

    public async Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        try
        {
            if (context.IsFreshInstall)
            {
                // Fresh install: remove what we just registered
                await adapter.UnregisterAsync(context, cancellationToken);
                context.Logger.LogInformation(
                    "[{App}@{Server}] Rollback: application registration removed.",
                    context.ApplicationName, context.TargetServer);
            }
            else
            {
                // Update: re-register with the backed-up (previous) configuration.
                // The files were restored by DeployFilesStep.RollbackAsync, so re-registering
                // against the same paths picks up the restored binaries automatically.
                await adapter.RegisterAsync(context, cancellationToken);
                context.Logger.LogInformation(
                    "[{App}@{Server}] Rollback: application re-registered with previous configuration.",
                    context.ApplicationName, context.TargetServer);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex,
                "[{App}@{Server}] Rollback: failed to revert application registration.",
                context.ApplicationName, context.TargetServer);
        }
    }
}
