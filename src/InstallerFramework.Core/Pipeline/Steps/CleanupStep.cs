using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 5 — Cleanup.
/// Removes the staging directory on the target server.
/// Prunes backups older than the configured retention period.
/// This step does NOT roll back on failure — it's best-effort cleanup only.
/// </summary>
public sealed class CleanupStep : IDeploymentStep
{
    public string Name => "Cleanup";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        // Remove staging dir
        if (context.RemoteStagingDirectory is not null)
        {
            var stagingParent = Path.GetDirectoryName(context.RemoteStagingDirectory)!;
            try
            {
                await context.Remote.DeleteDirectoryAsync(stagingParent, recursive: true, cancellationToken);
                context.Logger.LogDebug("[{App}@{Server}] Staging directory removed.", context.ApplicationName, context.TargetServer);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "[{App}@{Server}] Could not remove staging directory — not critical.", context.ApplicationName, context.TargetServer);
            }
        }

        // Prune old backups
        var appBackupRoot = Path.Combine(context.BackupRoot, context.ApplicationName);
        var retentionDays = context.BackupRetentionDays;

        var pruneScript = $@"
            $backupRoot = '{EscapePs(appBackupRoot)}'
            $retentionDays = {retentionDays}
            $cutoff = (Get-Date).AddDays(-$retentionDays)

            if (-not (Test-Path $backupRoot)) {{ return }}

            Get-ChildItem -Path $backupRoot -Directory |
                Where-Object {{ $_.CreationTime -lt $cutoff }} |
                ForEach-Object {{
                    Remove-Item -Path $_.FullName -Recurse -Force
                    Write-Output ""Pruned backup: $($_.Name)""
                }}
        ";

        try
        {
            var result = await context.Remote.ExecuteScriptAsync(pruneScript, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Output))
                context.Logger.LogInformation("[{App}@{Server}] {PruneOutput}", context.ApplicationName, context.TargetServer, result.Output.Trim());
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "[{App}@{Server}] Backup pruning failed — not critical.", context.ApplicationName, context.TargetServer);
        }

        return StepResult.Ok();
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Best-effort only — cleanup failure doesn't roll back.

    private static string EscapePs(string s) => s.Replace("'", "''");
}
