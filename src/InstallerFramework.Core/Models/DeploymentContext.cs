using InstallerFramework.Core.Remote;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Models;

/// <summary>
/// Carries all state for a single application deployment to a single server.
/// Passed through the pipeline steps — steps read from it and write their outputs back to it.
/// </summary>
public sealed class DeploymentContext
{
    // --- Immutable inputs ---

    public required ApplicationManifest ApplicationManifest { get; init; }
    public required ApplicationDeployment Deployment { get; init; }
    public required string TargetServer { get; init; }
    public required IRemoteExecutor Remote { get; init; }
    public required ILogger Logger { get; init; }
    public required string EnvironmentName { get; init; }
    public required string BackupRoot { get; init; }
    public required int BackupRetentionDays { get; init; }

    /// <summary>
    /// Merged parameter set: shared environment parameters overlaid with this deployment's
    /// own parameters (deployment-specific values win on conflict).
    /// Pipeline steps should always read from this rather than Deployment.Parameters directly.
    /// </summary>
    public required IReadOnlyDictionary<string, string> EffectiveParameters { get; init; }

    /// <summary>
    /// Environment-level default package source directory (from 'default-package-source' in the
    /// environment manifest). Falls between the per-deployment override and the application
    /// manifest default in the resolution chain.
    /// </summary>
    public required string? DefaultPackageSource { get; init; }

    // --- Mutable state set during pipeline execution ---

    /// <summary>Full path to the resolved .nupkg file on the deployment machine.</summary>
    public string? ResolvedPackagePath { get; set; }

    /// <summary>Path on the TARGET server where the package was extracted for staging.</summary>
    public string? RemoteStagingDirectory { get; set; }

    /// <summary>Path on the TARGET server where the backup of the previous installation was created.</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Whether the application was running before we stopped it (determines whether to restart on rollback).</summary>
    public bool ApplicationWasRunning { get; set; }

    /// <summary>The version string of the currently installed version (if any) — populated during pre-flight.</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>True if this application was not installed at all before this deployment.</summary>
    public bool IsFreshInstall { get; set; }

    /// <summary>
    /// The resolved identity this application runs as.
    /// Derived by applying the resolution chain:
    ///   per-application manifest account → environment default account → built-in fallback.
    /// Adapters and validation steps should always read the account from here rather than
    /// directly from the manifest, so the environment default is always honoured.
    /// </summary>
    public required ServiceAccount EffectiveAccount { get; init; }

    /// <summary>
    /// Password for <see cref="EffectiveAccount"/> when it is a regular domain account.
    /// Null for gMSA accounts (AD manages the secret) and built-in identities (no password).
    /// Collected once per unique account name before any server-side changes begin.
    /// Sourced from --password CLI flag → INSTALLER_DOMAIN_PASSWORD env var → interactive prompt.
    /// </summary>
    public string? DomainPassword { get; set; }

    /// <summary>
    /// When true, the pipeline will NOT roll back completed steps on failure.
    /// The server is left in whatever state it reached, allowing the operator to
    /// inspect configuration files, logs, and application state before retrying.
    /// Default: false (rollback is enabled).
    /// </summary>
    public bool NoRollback { get; set; }

    /// <summary>
    /// Set to true when the pipeline is running an uninstall rather than an install.
    /// Steps that are only meaningful for installs (parameter validation, version checks,
    /// configuration, etc.) should skip their work when this flag is set.
    /// </summary>
    public bool IsUninstall { get; set; }

    // --- Convenience properties ---

    public string ApplicationName => ApplicationManifest.Application.Name;

    /// <summary>
    /// Resolved IIS virtual application path: the deployment-block
    /// <c>application-name-override</c> if set, otherwise the application manifest's
    /// <c>iis.application-path</c>.  Always has a leading slash.
    /// Pipeline steps and adapters should read this instead of
    /// <c>ApplicationManifest.Application.Iis.ApplicationPath</c> directly.
    /// </summary>
    public string EffectiveIisApplicationPath =>
        !string.IsNullOrWhiteSpace(Deployment.ApplicationNameOverride)
            ? "/" + Deployment.ApplicationNameOverride.TrimStart('/')
            : ApplicationManifest.Application.Iis?.ApplicationPath ?? string.Empty;

    /// <summary>
    /// Resolved target version: the deployment-block version if set, otherwise the
    /// application manifest's own version field.  Pipeline steps should always read
    /// this rather than <c>Deployment.Version</c> directly.
    /// </summary>
    public string TargetVersion =>
        !string.IsNullOrWhiteSpace(Deployment.Version)
            ? Deployment.Version
            : ApplicationManifest.Application.Version ?? string.Empty;
    public ApplicationType AppType => ApplicationManifest.Application.Type;

    public bool IsLocalhost =>
        TargetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        TargetServer.Equals(".", StringComparison.OrdinalIgnoreCase) ||
        TargetServer.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}
