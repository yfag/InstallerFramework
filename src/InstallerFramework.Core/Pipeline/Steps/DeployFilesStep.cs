using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 3 — Install.
/// Moves staged, configured files to the final install directory on the target server.
/// Uses robocopy for reliable Windows file copy (retries, full attribute preservation).
/// On rollback, restores files from the backup created in CreateBackupStep.
/// </summary>
public sealed class DeployFilesStep : IDeploymentStep
{
    public string Name => "Deploy Files";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.RemoteStagingDirectory is null)
            return StepResult.Fail("Staging directory not set.");

        var targetDir = GetTargetDirectory(context);
        if (targetDir is null)
            return StepResult.Fail("Cannot determine target install directory.");

        try
        {
            // Ensure target directory exists
            await context.Remote.CreateDirectoryAsync(targetDir, cancellationToken);

            // Use robocopy: /E = include subdirs, /PURGE = delete files in dest not in source,
            // /COPY:DAT = copy Data+Attributes+Timestamps only (NOT security/ACLs).
            // /COPYALL would copy the source ACLs from the temp staging directory, producing
            // files that IIS and operators cannot read. Omitting security lets the destination
            // files inherit their ACLs from the parent directory as intended.
            var script = $@"
                $src = '{EscapePs(context.RemoteStagingDirectory)}'
                $dst = '{EscapePs(targetDir)}'

                $result = robocopy $src $dst /E /PURGE /COPY:DAT /R:2 /W:1 /NP /NJH /NJS
                Write-Output ""Robocopy exit code: $LASTEXITCODE""
                if ($LASTEXITCODE -gt 7) {{
                    throw ""File deployment failed. Robocopy exit code: $LASTEXITCODE""
                }}
            ";

            var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);
            if (!result.Success)
                return StepResult.Fail($"File deployment failed: {result.Errors}");

            // Write the version marker file used by CheckConnectivityStep on future runs
            var versionFilePath = Path.Combine(targetDir, ".installer-version");
            await context.Remote.WriteFileAsync(versionFilePath, context.TargetVersion, cancellationToken);

            context.Logger.LogInformation(
                "[{App}@{Server}] Files deployed to {Dir}.",
                context.ApplicationName, context.TargetServer, targetDir);

            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"File deployment error: {ex.Message}");
        }
    }

    public async Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.BackupDirectory is null || context.IsFreshInstall)
        {
            // Fresh install rollback: remove the target directory entirely
            var targetDir = GetTargetDirectory(context);
            if (targetDir is not null)
            {
                try
                {
                    await context.Remote.DeleteDirectoryAsync(targetDir, recursive: true, cancellationToken);
                    context.Logger.LogInformation(
                        "[{App}@{Server}] Rollback: target directory removed (fresh install).",
                        context.ApplicationName, context.TargetServer);
                }
                catch (Exception ex)
                {
                    context.Logger.LogError(ex,
                        "[{App}@{Server}] Rollback: failed to remove target directory.",
                        context.ApplicationName, context.TargetServer);
                }
            }
            return;
        }

        try
        {
            var targetDir = GetTargetDirectory(context);
            if (targetDir is null) return;

            var script = $@"
                $src = '{EscapePs(context.BackupDirectory)}'
                $dst = '{EscapePs(targetDir)}'
                $result = robocopy $src $dst /E /PURGE /COPY:DAT /R:2 /W:1 /NP /NJH /NJS
                if ($LASTEXITCODE -gt 7) {{
                    throw ""Rollback copy failed. Robocopy exit code: $LASTEXITCODE""
                }}
            ";

            var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);
            if (!result.Success)
                context.Logger.LogError(
                    "[{App}@{Server}] Rollback: file restore from backup FAILED: {Errors}",
                    context.ApplicationName, context.TargetServer, result.Errors);
            else
                context.Logger.LogInformation(
                    "[{App}@{Server}] Rollback: files restored from backup.",
                    context.ApplicationName, context.TargetServer);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex,
                "[{App}@{Server}] Rollback: exception during file restore.",
                context.ApplicationName, context.TargetServer);
        }
    }

    private static string? GetTargetDirectory(DeploymentContext context)
    {
        var app = context.ApplicationManifest.Application;
        return app.Type switch
        {
            ApplicationType.WindowsService => app.Service?.InstallDirectory,
            ApplicationType.IisApplication => app.Iis?.PhysicalPath,
            _ => null
        };
    }

    private static string EscapePs(string path) => path.Replace("'", "''");
}
