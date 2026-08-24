using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 2 — Snapshot.
/// Copies the current installation directory to a timestamped backup location.
/// This is the rollback point — all subsequent file operations are reversible from here.
/// Skipped on fresh installs (nothing to back up).
/// </summary>
public sealed class CreateBackupStep : IDeploymentStep
{
    public string Name => "Create Backup";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.IsFreshInstall)
        {
            context.Logger.LogDebug("[{App}@{Server}] Fresh install — no backup needed.", context.ApplicationName, context.TargetServer);
            return StepResult.Ok();
        }

        var installDir = GetInstallDirectory(context);
        if (installDir is null)
            return StepResult.Fail("Cannot determine install directory for backup.");

        var dirExists = await context.Remote.DirectoryExistsAsync(installDir, cancellationToken);
        if (!dirExists)
        {
            context.Logger.LogWarning(
                "[{App}@{Server}] Install directory '{Dir}' does not exist — treating as fresh install.",
                context.ApplicationName, context.TargetServer, installDir);
            context.IsFreshInstall = true;
            return StepResult.Ok();
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(
            context.BackupRoot,
            context.ApplicationName,
            $"{context.PreviousVersion ?? "unknown"}_{timestamp}");

        try
        {
            await context.Remote.CreateDirectoryAsync(
                Path.GetDirectoryName(backupDir)!, cancellationToken);

            // Use robocopy via PS for reliable directory copy on Windows
            var robocopyScript = $@"
                $src = '{EscapePs(installDir)}'
                $dst = '{EscapePs(backupDir)}'
                $result = robocopy $src $dst /E /COPYALL /R:2 /W:1 /NP /NJH /NJS
                # Robocopy exit codes 0-7 are success
                if ($LASTEXITCODE -gt 7) {{
                    throw ""Robocopy failed with exit code $LASTEXITCODE""
                }}
            ";

            var result = await context.Remote.ExecuteScriptAsync(robocopyScript, cancellationToken: cancellationToken);
            if (!result.Success)
                return StepResult.Fail($"Backup copy failed: {result.Errors}");

            context.BackupDirectory = backupDir;

            context.Logger.LogInformation(
                "[{App}@{Server}] Backup created: {BackupDir}",
                context.ApplicationName, context.TargetServer, backupDir);

            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Failed to create backup: {ex.Message}");
        }
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        // The backup itself is our rollback asset — we don't delete it during rollback.
        // It will be cleaned up by the retention policy after successful deployment.
        context.Logger.LogDebug("[{App}@{Server}] Backup retained for rollback use.", context.ApplicationName, context.TargetServer);
        return Task.CompletedTask;
    }

    private static string? GetInstallDirectory(DeploymentContext context)
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
