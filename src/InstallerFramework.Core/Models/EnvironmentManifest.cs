using YamlDotNet.Serialization;

namespace InstallerFramework.Core.Models;

/// <summary>
/// Describes a deployment environment — which applications go on which servers,
/// at what version, and with what environment-specific parameter values.
/// This replaces the install.json + CSV update approach.
/// No passwords. No plain-text secrets. Server names and parameter values only.
/// </summary>
public class EnvironmentManifest
{
    [YamlMember(Alias = "environment")]
    public EnvironmentConfig Environment { get; set; } = new();
}

public class EnvironmentConfig
{
    /// <summary>Human-readable environment label, e.g. "Production", "Test-Alpha".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Directory where .application.yaml files are located.
    /// Relative to the environment manifest file, or absolute.
    /// Defaults to the same directory as the environment manifest.
    /// </summary>
    [YamlMember(Alias = "manifests-path")]
    public string ManifestsPath { get; set; } = ".";

    /// <summary>
    /// Default package source override for this environment.
    /// Overrides the source in each application manifest if set.
    /// </summary>
    [YamlMember(Alias = "default-package-source")]
    public string? DefaultPackageSource { get; set; }

    /// <summary>
    /// Default number of days to retain installation backups.
    /// Can be overridden per deployment.
    /// </summary>
    [YamlMember(Alias = "backup-retention-days")]
    public int BackupRetentionDays { get; set; } = 7;

    /// <summary>Root directory on target servers where backups are stored.</summary>
    [YamlMember(Alias = "backup-root")]
    public string BackupRoot { get; set; } = @"C:\InstallBackups";

    /// <summary>
    /// Parameter values shared across all applications in this environment.
    /// These are merged with each deployment's own parameters at deploy time —
    /// deployment-specific values take precedence over shared ones.
    ///
    /// Use this for values that are the same in every app:
    ///   AppSettings:ConfigServer.BaseUrl, AppSettings:ConfigServer.Scopes, etc.
    ///
    /// Any key listed here counts toward satisfying an application's required-parameters,
    /// so you don't need to repeat it in every deployment block.
    /// </summary>
    [YamlMember(Alias = "shared-parameters")]
    public Dictionary<string, string> SharedParameters { get; set; } = [];

    /// <summary>
    /// Optional domain account configuration.
    /// Set <c>use-gmsa: false</c> when applications run under regular domain accounts
    /// that require a password (rather than gMSA accounts which are password-free).
    /// When absent or <c>use-gmsa: true</c> (the default), no password is collected.
    /// </summary>
    [YamlMember(Alias = "domain-account")]
    public DomainAccountConfig? DomainAccount { get; set; }

    public List<ApplicationDeployment> Deployments { get; set; } = [];
}

public class ApplicationDeployment
{
    /// <summary>Must match ApplicationManifest.Application.Name exactly.</summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>
    /// NuGet version string, e.g. "2.1.0" or "2.1.0-rc1".
    /// Optional: when omitted the version in the application manifest (<c>version:</c> field)
    /// is used.  Set this only to pin a specific version in this environment that differs
    /// from the application manifest default.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Target servers for installation.
    /// Removing a server from this list will cause 'installer diff' to flag it for uninstall.
    /// </summary>
    public List<string> Servers { get; set; } = [];

    /// <summary>
    /// Environment-specific parameter values.
    /// Keys must satisfy all RequiredParameters declared in the application manifest.
    /// Values are substituted into the config template or overlay.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>Overrides the default package source for this specific application.</summary>
    [YamlMember(Alias = "package-source")]
    public string? PackageSource { get; set; }

    /// <summary>Overrides the environment-level backup retention for this specific application.</summary>
    [YamlMember(Alias = "backup-retention-days")]
    public int? BackupRetentionDays { get; set; }

    /// <summary>
    /// Overrides the IIS virtual application name (path) for this deployment.
    /// Useful when the package ID contains a prefix that should be stripped from the URL.
    /// Example: package "Elements.ConfigServer" normally becomes /Elements.ConfigServer;
    /// setting this to "ConfigServer" makes it /ConfigServer instead.
    /// Only applies to IIS applications — ignored for Windows Services.
    /// </summary>
    [YamlMember(Alias = "application-name-override")]
    public string? ApplicationNameOverride { get; set; }

    /// <summary>Optional health check to run after installation to verify the application started correctly.</summary>
    [YamlMember(Alias = "health-check")]
    public HealthCheckConfig? HealthCheck { get; set; }
}

public class HealthCheckConfig
{
    /// <summary>HTTP/HTTPS URL to probe after start, e.g. "http://localhost/MyApp/health".</summary>
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "timeout-seconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [YamlMember(Alias = "retry-count")]
    public int RetryCount { get; set; } = 5;

    [YamlMember(Alias = "retry-interval-seconds")]
    public int RetryIntervalSeconds { get; set; } = 5;

    /// <summary>HTTP status code(s) that count as healthy, e.g. "200" or "200,204".</summary>
    [YamlMember(Alias = "expected-status")]
    public string ExpectedStatus { get; set; } = "200";
}

/// <summary>
/// Describes the default domain account used by applications in this environment.
///
/// The account specified here becomes the default identity for every Windows Service and
/// IIS application pool that does not set its own <c>account</c> field.  Individual
/// application manifests can still override it per-application.
///
/// Password collection rules:
/// <list type="bullet">
///   <item>gMSA accounts (name ends with <c>$</c>) — no password; AD manages it.</item>
///   <item>Built-in accounts (LocalSystem, NetworkService, …) — no password.</item>
///   <item>Regular domain accounts — the installer prompts once per unique account name.</item>
/// </list>
///
/// <c>use-gmsa</c> is retained for backward compatibility but is now optional when
/// <c>account</c> is set (the account name itself indicates the type).
/// </summary>
public class DomainAccountConfig
{
    /// <summary>
    /// Default account name for all service and app pool identities in this environment.
    /// Examples:
    ///   <c>DOMAIN\svc-myapp$</c>  — gMSA (no password)
    ///   <c>DOMAIN\svc-myapp</c>   — regular domain account (password required)
    ///   <c>LocalSystem</c>        — built-in (no password)
    /// When omitted, individual application manifests must set their own account, or the
    /// built-in fallback is used (LocalSystem for services, ApplicationPoolIdentity for IIS).
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Retained for backward compatibility.
    /// When <see cref="Account"/> is set the installer infers the account type from the
    /// name (trailing <c>$</c> = gMSA) and this flag has no effect on password collection.
    /// True  → Group Managed Service Account (gMSA); no password needed, AD manages it.
    /// False → Regular domain account; the installer will prompt for a password.
    /// </summary>
    [YamlMember(Alias = "use-gmsa")]
    public bool UseGmsa { get; set; } = true;
}
