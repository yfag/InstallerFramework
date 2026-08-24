using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Used in the uninstall pipeline only.
/// Removes the service registration (sc.exe delete) or IIS application/app pool.
/// Does NOT remove files — that is handled by a separate cleanup if desired.
/// </summary>
public sealed class UnregisterApplicationStep : IDeploymentStep
{
    private readonly IApplicationAdapterFactory _adapterFactory;

    public UnregisterApplicationStep(IApplicationAdapterFactory adapterFactory) => _adapterFactory = adapterFactory;

    public string Name => "Unregister Application";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        try
        {
            await adapter.UnregisterAsync(context, cancellationToken);
            context.Logger.LogInformation(
                "[{App}@{Server}] Application unregistered.",
                context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Unregistration failed: {ex.Message}");
        }
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Uninstall pipeline doesn't roll back
}
