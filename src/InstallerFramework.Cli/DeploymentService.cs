using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Configuration;
using InstallerFramework.Core.Models;
using InstallerFramework.Core.Packages;
using InstallerFramework.Core.Pipeline;
using InstallerFramework.Core.Pipeline.Steps;
using InstallerFramework.Core.Remote;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Cli;

/// <summary>
/// Orchestrates the full deployment workflow for the CLI commands.
/// Encapsulates pipeline construction and per-server execution.
/// </summary>
public sealed class DeploymentService
{
    private readonly ManifestLoader _manifestLoader;
    private readonly ManifestValidator _manifestValidator;
    private readonly NuGetPackageResolver _packageResolver;
    private readonly ConfigurationMerger _configMerger;
    private readonly IApplicationAdapterFactory _adapterFactory;
    private readonly ILoggerFactory _loggerFactory;

    public DeploymentService(
        ManifestLoader manifestLoader,
        ManifestValidator manifestValidator,
        NuGetPackageResolver packageResolver,
        ConfigurationMerger configMerger,
        IApplicationAdapterFactory adapterFactory,
        ILoggerFactory loggerFactory)
    {
        _manifestLoader    = manifestLoader;
        _manifestValidator = manifestValidator;
        _packageResolver   = packageResolver;
        _configMerger      = configMerger;
        _adapterFactory    = adapterFactory;
        _loggerFactory     = loggerFactory;
    }

    /// <summary>
    /// Returns the distinct domain user accounts that need a password across the set of
    /// deployments matched by the optional filters.
    ///
    /// Accounts are returned in a stable order: the environment-level default account first
    /// (if it needs a password), followed by any per-application override accounts that differ
    /// from the default.  gMSA accounts (name ends with <c>$</c>) and built-in accounts
    /// (LocalSystem, NetworkService, etc.) are never included.
    /// </summary>
    /// <param name="environmentManifestPath">Path to the environment YAML file.</param>
    /// <param name="applicationFilter">If set, only inspect deployments for this application.</param>
    /// <param name="serverFilter">Unused here — kept for API symmetry with <see cref="InstallAsync"/>.</param>
    public IReadOnlyList<(string AccountName, bool IsEnvironmentDefault)> GetAccountsNeedingPasswords(
        string environmentManifestPath,
        string? applicationFilter = null,
        string? serverFilter = null)
    {
        var envManifest  = _manifestLoader.LoadEnvironmentManifest(environmentManifestPath);
        var appManifests = _manifestLoader.LoadApplicationManifests(envManifest, environmentManifestPath);

        var envDefaultAccount = envManifest.Environment.DomainAccount?.Account;
        var result = new List<(string AccountName, bool IsEnvironmentDefault)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Environment-level default (if it is a domain user account).
        if (!string.IsNullOrWhiteSpace(envDefaultAccount))
        {
            var acct = ServiceAccount.FromName(envDefaultAccount);
            if (!acct.IsBuiltIn && !acct.IsGmsa)
            {
                result.Add((envDefaultAccount, true));
                seen.Add(envDefaultAccount);
            }
        }

        // 2. Per-application overrides that differ from the environment default.
        var deployments = envManifest.Environment.Deployments
            .Where(d => applicationFilter is null ||
                        d.Application.Equals(applicationFilter, StringComparison.OrdinalIgnoreCase));

        foreach (var deployment in deployments)
        {
            if (!appManifests.TryGetValue(deployment.Application, out var appManifest)) continue;

            var effective = ResolveEffectiveAccount(appManifest, envManifest.Environment);
            if (!effective.IsBuiltIn && !effective.IsGmsa && seen.Add(effective.AccountName))
                result.Add((effective.AccountName, IsEnvironmentDefault: false));
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Runs manifest validation without touching any server.
    /// Returns a list of errors (empty = valid).
    /// </summary>
    public IReadOnlyList<string> Validate(string environmentManifestPath)
    {
        var envManifest  = _manifestLoader.LoadEnvironmentManifest(environmentManifestPath);
        var appManifests = _manifestLoader.LoadApplicationManifests(envManifest, environmentManifestPath);
        return _manifestValidator.Validate(envManifest, appManifests);
    }

    /// <summary>
    /// Installs or updates applications across their configured servers.
    /// By default skips any application/server pair already at the correct version.
    /// Pass force=true to redeploy everything regardless of current state.
    /// </summary>
    /// <param name="domainPasswords">
    /// Map of account name → password for every domain user account that requires one.
    /// Build this with <see cref="GetAccountsNeedingPasswords"/> before calling this method.
    /// Null or empty is fine when only gMSA or built-in accounts are in use.
    /// </param>
    public async Task<EnvironmentDeploymentSummary> InstallAsync(
        string environmentManifestPath,
        string? applicationFilter = null,
        string? serverFilter = null,
        bool force = false,
        IReadOnlyDictionary<string, string>? domainPasswords = null,
        bool noRollback = false,
        CancellationToken cancellationToken = default)
    {
        var envManifest  = _manifestLoader.LoadEnvironmentManifest(environmentManifestPath);
        var appManifests = _manifestLoader.LoadApplicationManifests(envManifest, environmentManifestPath);

        // Validate before touching any server
        var errors = _manifestValidator.Validate(envManifest, appManifests);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Manifest validation failed:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(e => $"  - {e}")));

        var deployments = envManifest.Environment.Deployments
            .Where(d => applicationFilter is null ||
                        d.Application.Equals(applicationFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var results = new List<DeploymentResult>();
        var started = DateTimeOffset.Now;

        foreach (var deployment in deployments)
        {
            var appManifest = appManifests[deployment.Application];
            var servers = deployment.Servers
                .Where(s => serverFilter is null ||
                            s.Equals(serverFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var server in servers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await DeployToServerAsync(
                    envManifest.Environment, appManifest, deployment, server, force, domainPasswords, noRollback, cancellationToken);
                results.Add(result);
            }
        }

        return new EnvironmentDeploymentSummary
        {
            EnvironmentName = envManifest.Environment.Name,
            StartedAt       = started,
            CompletedAt     = DateTimeOffset.Now,
            Results         = results
        };
    }

    /// <summary>
    /// Uninstalls one or all applications from their configured servers.
    /// Pass <paramref name="applicationName"/> to target a single application;
    /// pass <c>null</c> to uninstall every application in the environment manifest.
    /// </summary>
    public async Task<EnvironmentDeploymentSummary> UninstallAsync(
        string environmentManifestPath,
        string? applicationName = null,
        string? serverFilter = null,
        CancellationToken cancellationToken = default)
    {
        var envManifest  = _manifestLoader.LoadEnvironmentManifest(environmentManifestPath);
        var appManifests = _manifestLoader.LoadApplicationManifests(envManifest, environmentManifestPath);

        var deployments = envManifest.Environment.Deployments
            .Where(d => applicationName is null ||
                        d.Application.Equals(applicationName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deployments.Count == 0)
            throw new ArgumentException(
                applicationName is not null
                    ? $"Application '{applicationName}' not found in environment manifest."
                    : "No applications found in environment manifest.");

        var results = new List<DeploymentResult>();
        var started  = DateTimeOffset.Now;

        foreach (var deployment in deployments)
        {
            var appManifest = appManifests[deployment.Application];
            var servers = deployment.Servers
                .Where(s => serverFilter is null || s.Equals(serverFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var server in servers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await UninstallFromServerAsync(
                    envManifest.Environment, appManifest, deployment, server, cancellationToken);
                results.Add(result);
            }
        }

        return new EnvironmentDeploymentSummary
        {
            EnvironmentName = envManifest.Environment.Name,
            StartedAt       = started,
            CompletedAt     = DateTimeOffset.Now,
            Results         = results
        };
    }

    // --- Private helpers ---

    private async Task<DeploymentResult> DeployToServerAsync(
        EnvironmentConfig environment,
        ApplicationManifest appManifest,
        ApplicationDeployment deployment,
        string server,
        bool force,
        IReadOnlyDictionary<string, string>? domainPasswords,
        bool noRollback,
        CancellationToken cancellationToken)
    {
        var appName = appManifest.Application.Name;
        var logger  = _loggerFactory.CreateLogger<DeploymentOrchestrator>();
        var appLogger = _loggerFactory.CreateLogger(appName);

        var targetVersion = ResolveEffectiveVersion(appManifest, deployment);

        // --- Version pre-check (via admin share — no WinRM session needed) ---
        // Skip the full pipeline if the correct version is already installed,
        // unless --force was specified.
        if (!force)
        {
            var installedVersion = ReadInstalledVersionFromAdminShare(appManifest, server);
            if (installedVersion is not null &&
                installedVersion.Equals(targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                appLogger.LogInformation(
                    "[{App}@{Server}] Already at v{Version} — skipping.",
                    appName, server, targetVersion);
                return DeploymentResult.AlreadyUpToDate(appName, server, targetVersion);
            }

            if (installedVersion is not null)
                appLogger.LogInformation(
                    "[{App}@{Server}] Installed: v{Installed} → Target: v{Target}. Deploying.",
                    appName, server, installedVersion, targetVersion);
            else
                appLogger.LogInformation(
                    "[{App}@{Server}] Not currently installed. Deploying v{Version}.",
                    appName, server, targetVersion);
        }
        else
        {
            appLogger.LogInformation(
                "[{App}@{Server}] --force specified — redeploying v{Version} regardless of installed state.",
                appName, server, targetVersion);
        }

        // --- Full deployment pipeline ---
        await using var remote = server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? (IRemoteExecutor)new LocalExecutor(_loggerFactory.CreateLogger<LocalExecutor>())
            : await WinRmExecutor.ConnectAsync(
                server, _loggerFactory.CreateLogger<WinRmExecutor>(), cancellationToken: cancellationToken);

        var retentionDays = deployment.BackupRetentionDays ?? environment.BackupRetentionDays;

        // Merge shared + deployment-specific parameters.
        // Shared parameters provide defaults; deployment-specific values override them.
        var effectiveParameters = new Dictionary<string, string>(
            environment.SharedParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in deployment.Parameters)
            effectiveParameters[k] = v;

        var effectiveAccount = ResolveEffectiveAccount(appManifest, environment);
        var domainPassword   = domainPasswords?.GetValueOrDefault(effectiveAccount.AccountName);

        var context = new DeploymentContext
        {
            ApplicationManifest = appManifest,
            Deployment          = deployment,
            TargetServer        = server,
            Remote              = remote,
            Logger              = appLogger,
            EnvironmentName     = environment.Name,
            BackupRoot            = environment.BackupRoot,
            BackupRetentionDays   = retentionDays,
            EffectiveParameters   = effectiveParameters,
            DefaultPackageSource  = environment.DefaultPackageSource,
            EffectiveAccount      = effectiveAccount,
            DomainPassword        = domainPassword,
            NoRollback            = noRollback
        };

        var pipeline     = BuildInstallPipeline();
        var orchestrator = new DeploymentOrchestrator(pipeline, logger);
        return await orchestrator.ExecuteAsync(context, cancellationToken);
    }

    private async Task<DeploymentResult> UninstallFromServerAsync(
        EnvironmentConfig environment,
        ApplicationManifest appManifest,
        ApplicationDeployment deployment,
        string server,
        CancellationToken cancellationToken)
    {
        var appName   = appManifest.Application.Name;
        var logger    = _loggerFactory.CreateLogger<DeploymentOrchestrator>();
        var appLogger = _loggerFactory.CreateLogger(appName);

        // Fast pre-check via admin share — no WinRM session required.
        // If the .installer-version marker is absent the application was never deployed
        // by this tool (or was already removed), so there is nothing to uninstall.
        var installedVersion = ReadInstalledVersionFromAdminShare(appManifest, server);
        if (installedVersion is null)
        {
            appLogger.LogInformation(
                "[{App}@{Server}] Not installed — nothing to uninstall.",
                appName, server);
            return DeploymentResult.NotInstalled(appName, server);
        }

        appLogger.LogInformation(
            "[{App}@{Server}] Found installed version v{Version} — proceeding with uninstall.",
            appName, server, installedVersion);

        await using var remote = server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? (IRemoteExecutor)new LocalExecutor(_loggerFactory.CreateLogger<LocalExecutor>())
            : await WinRmExecutor.ConnectAsync(
                server, _loggerFactory.CreateLogger<WinRmExecutor>(), cancellationToken: cancellationToken);

        var context = new DeploymentContext
        {
            ApplicationManifest = appManifest,
            Deployment          = deployment,
            TargetServer        = server,
            Remote              = remote,
            Logger              = appLogger,
            EnvironmentName     = environment.Name,
            BackupRoot           = environment.BackupRoot,
            BackupRetentionDays  = environment.BackupRetentionDays,
            EffectiveParameters  = deployment.Parameters,  // Uninstall doesn't use config — no need to merge
            DefaultPackageSource = environment.DefaultPackageSource,
            EffectiveAccount     = ResolveEffectiveAccount(appManifest, environment),
            IsUninstall          = true
        };

        var pipeline     = BuildUninstallPipeline();
        var orchestrator = new DeploymentOrchestrator(pipeline, logger);
        return await orchestrator.ExecuteAsync(context, cancellationToken);
    }

    /// <summary>
    /// Reads the .installer-version marker file directly via the admin share (\\SERVER\C$\...).
    /// This avoids opening a full WinRM session just to decide whether to skip.
    /// Returns null if the file doesn't exist or can't be read (treat as not installed).
    /// </summary>
    private static string? ReadInstalledVersionFromAdminShare(
        ApplicationManifest appManifest,
        string server)
    {
        var installDir = appManifest.Application.Type switch
        {
            ApplicationType.WindowsService => appManifest.Application.Service?.InstallDirectory,
            ApplicationType.IisApplication => appManifest.Application.Iis?.PhysicalPath,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(installDir)) return null;

        try
        {
            var uncVersionFile = ToAdminSharePath(server, Path.Combine(installDir, ".installer-version"));
            return File.Exists(uncVersionFile)
                ? File.ReadAllText(uncVersionFile).Trim()
                : null;
        }
        catch
        {
            // Can't read the file — treat as not installed and let the pipeline handle it
            return null;
        }
    }

    /// <summary>
    /// Resolves the service/pool identity for a deployment using the three-level chain:
    /// <list type="number">
    ///   <item>Per-application account set in the application manifest.</item>
    ///   <item>Environment-level default from <c>domain-account.account</c>.</item>
    ///   <item>Built-in fallback: <c>LocalSystem</c> for services, <c>ApplicationPoolIdentity</c> for IIS.</item>
    /// </list>
    /// </summary>
    internal static ServiceAccount ResolveEffectiveAccount(
        ApplicationManifest appManifest,
        EnvironmentConfig environment)
    {
        // Step 1: per-app explicit override (null = not set in the manifest file)
        var perAppAccount = appManifest.Application.Type switch
        {
            ApplicationType.WindowsService => appManifest.Application.Service?.Account,
            ApplicationType.IisApplication => appManifest.Application.Iis?.AppPool.Account,
            _                              => null
        };

        // Step 2 & 3: environment default → built-in fallback
        var builtInFallback = appManifest.Application.Type == ApplicationType.WindowsService
            ? "LocalSystem"
            : "ApplicationPoolIdentity";

        var accountName = perAppAccount
            ?? environment.DomainAccount?.Account
            ?? builtInFallback;

        return ServiceAccount.FromName(accountName);
    }

    /// <summary>
    /// Resolves the effective deployment version.
    /// Priority: deployment-block version → application manifest version.
    /// The deployment block can be used to pin a specific version per environment;
    /// the application manifest version is the default set by 'installer prepare'.
    /// </summary>
    internal static string ResolveEffectiveVersion(
        ApplicationManifest appManifest,
        ApplicationDeployment deployment) =>
        !string.IsNullOrWhiteSpace(deployment.Version)
            ? deployment.Version
            : appManifest.Application.Version ?? string.Empty;

    private static string ToAdminSharePath(string server, string localPath)
    {
        if (localPath.Length < 3 || localPath[1] != ':')
            throw new ArgumentException($"Cannot convert to admin share path: '{localPath}'");

        var drive = localPath[0];
        var rest  = localPath.Length > 3 ? localPath[3..] : string.Empty;
        return $@"\\{server}\{drive}$\{rest}";
    }

    private IEnumerable<IDeploymentStep> BuildInstallPipeline() =>
    [
        new ValidateManifestStep(),
        new CheckConnectivityStep(),
        new ValidateDomainCredentialsStep(),     // must run before any server-side changes
        new ResolvePackageStep(_packageResolver),
        new StopApplicationStep(_adapterFactory),
        new CreateBackupStep(),
        new ExtractPackageStep(_packageResolver),
        new ApplyConfigurationStep(_configMerger),
        new DeployFilesStep(),
        new RegisterApplicationStep(_adapterFactory),
        new StartApplicationStep(_adapterFactory),
        new VerifyRunningStep(_adapterFactory),
        new CleanupStep()
    ];

    private IEnumerable<IDeploymentStep> BuildUninstallPipeline() =>
    [
        new ValidateManifestStep(),
        new CheckConnectivityStep(),
        new StopApplicationStep(_adapterFactory),
        new UnregisterApplicationStep(_adapterFactory),
        new RemoveInstallDirectoryStep(),    // deletes the install dir — also removes .installer-version
        new CleanupStep()
    ];
}
