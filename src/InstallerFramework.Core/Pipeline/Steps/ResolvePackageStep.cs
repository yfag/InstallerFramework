using InstallerFramework.Core.Models;
using InstallerFramework.Core.Packages;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 1 — Pre-flight.
/// Finds the .nupkg file for the requested version in the package source directory.
/// Validates it exists and is readable before any server contact.
/// No changes made.
/// </summary>
public sealed class ResolvePackageStep : IDeploymentStep
{
    private readonly NuGetPackageResolver _resolver;

    public ResolvePackageStep(NuGetPackageResolver resolver) => _resolver = resolver;

    public string Name => "Resolve Package";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var app = context.ApplicationManifest.Application;

        // Effective source: per-deployment override → environment default → application manifest default
        var source = context.Deployment.PackageSource
                     ?? context.DefaultPackageSource
                     ?? app.Package.Source;

        if (string.IsNullOrWhiteSpace(source))
            return StepResult.Fail("No package source configured. Set 'package.source' in the application manifest or 'package-source' in the environment deployment.");

        try
        {
            var packagePath = await _resolver.ResolveAsync(
                source,
                app.Package.Id,
                context.TargetVersion,
                cancellationToken);

            if (packagePath is null)
                return StepResult.Fail(
                    $"Package '{app.Package.Id}' version '{context.TargetVersion}' not found in '{source}'. " +
                    $"Expected file: {app.Package.Id}.{context.TargetVersion}.nupkg");

            context.ResolvedPackagePath = packagePath;

            context.Logger.LogInformation(
                "[{App}@{Server}] Resolved package: {Path}",
                context.ApplicationName, context.TargetServer, packagePath);

            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Error resolving package: {ex.Message}");
        }
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
