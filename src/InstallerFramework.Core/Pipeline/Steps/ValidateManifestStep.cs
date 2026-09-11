using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 1 — Pre-flight.
/// Validates that the environment manifest satisfies everything the application manifest requires.
/// No server contact. No file system changes. Safe to abort.
/// </summary>
public sealed class ValidateManifestStep : IDeploymentStep
{
    public string Name => "Validate Manifest";

    public Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var app = context.ApplicationManifest.Application;
        var deployment = context.Deployment;
        var errors = new List<string>();

        // Application-type-specific config must be present
        if (app.Type == ApplicationType.WindowsService && app.Service is null)
            errors.Add("Application type is WindowsService but no 'service' block is defined in the application manifest.");

        if (app.Type == ApplicationType.IisApplication && app.Iis is null)
            errors.Add("Application type is IisApplication but no 'iis' block is defined in the application manifest.");

        if (!context.IsUninstall)
        {
            // Required parameters must all be supplied — either in the deployment block
            // or in the environment's shared-parameters (both are merged into EffectiveParameters).
            // Uninstall skips this: no configuration is applied during removal.
            var missingParams = app.Configuration.RequiredParameters
                .Where(p => !context.EffectiveParameters.ContainsKey(p))
                .ToList();

            if (missingParams.Count > 0)
                errors.Add($"Missing required parameters: {string.Join(", ", missingParams)}");

            // Version must be resolvable from either the deployment block or the application manifest.
            if (string.IsNullOrWhiteSpace(context.TargetVersion))
                errors.Add("No version specified. Set 'version' in the application manifest or in the environment deployment block.");
        }

        // Windows Service: install directory must be set
        if (app.Type == ApplicationType.WindowsService && string.IsNullOrWhiteSpace(app.Service?.InstallDirectory))
            errors.Add("Service 'install-directory' is not set in the application manifest.");

        // IIS: physical path and application path must be set
        if (app.Type == ApplicationType.IisApplication)
        {
            if (string.IsNullOrWhiteSpace(app.Iis?.ApplicationPath))
                errors.Add("IIS 'application-path' is not set in the application manifest.");
            if (string.IsNullOrWhiteSpace(app.Iis?.PhysicalPath))
                errors.Add("IIS 'physical-path' is not set in the application manifest.");
            if (string.IsNullOrWhiteSpace(app.Iis?.AppPool?.Name))
                errors.Add("IIS app pool 'name' is not set in the application manifest.");
        }

        if (errors.Count > 0)
        {
            var message = $"Manifest validation failed:{Environment.NewLine}" +
                          string.Join(Environment.NewLine, errors.Select(e => $"  - {e}"));
            context.Logger.LogError("{Errors}", message);
            return Task.FromResult(StepResult.Fail(message));
        }

        context.Logger.LogDebug("[{App}] Manifest validation passed.", context.ApplicationName);
        return Task.FromResult(StepResult.Ok());
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // No state changed — nothing to undo.
}
