using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Used in the uninstall pipeline only.
/// Deletes the application's install directory from the target server after the service
/// or IIS application has been unregistered.
///
/// Removing the directory also deletes the <c>.installer-version</c> marker file, which
/// is what the pre-install and pre-uninstall checks use to detect whether the application
/// is present.  Without this step the marker would linger, causing subsequent uninstall
/// attempts to believe the application is still installed.
///
/// No rollback — this is a destructive cleanup step; the uninstall pipeline does not roll back.
/// </summary>
public sealed class RemoveInstallDirectoryStep : IDeploymentStep
{
    public string Name => "Remove Files";

    public async Task<StepResult> ExecuteAsync(
        DeploymentContext context,
        CancellationToken cancellationToken = default)
    {
        var installDir = context.ApplicationManifest.Application.Type switch
        {
            ApplicationType.WindowsService => context.ApplicationManifest.Application.Service?.InstallDirectory,
            ApplicationType.IisApplication => context.ApplicationManifest.Application.Iis?.PhysicalPath,
            _                              => null
        };

        if (string.IsNullOrWhiteSpace(installDir))
        {
            context.Logger.LogWarning(
                "[{App}@{Server}] Install directory not configured — skipping file removal.",
                context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }

        var escaped = installDir.Replace("'", "''");
        var script = $@"
if (Test-Path -LiteralPath '{escaped}') {{
    Remove-Item -LiteralPath '{escaped}' -Recurse -Force
    Write-Output 'Removed.'
}} else {{
    Write-Output 'Directory not found — already absent.'
}}
";
        var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);

        if (!result.Success)
            return StepResult.Fail($"Failed to remove install directory '{installDir}': {result.Errors}");

        context.Logger.LogInformation(
            "[{App}@{Server}] {Dir}: {Out}",
            context.ApplicationName, context.TargetServer, installDir, result.Output.Trim());

        return StepResult.Ok();
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Uninstall pipeline does not roll back.
}
