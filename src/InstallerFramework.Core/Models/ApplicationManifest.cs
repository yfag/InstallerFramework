using YamlDotNet.Serialization;

namespace InstallerFramework.Core.Models;

/// <summary>
/// Describes an application — what it is, how it runs, and what configuration it needs.
/// One file per application, version-controlled alongside the application.
/// Does NOT contain environment-specific values or server targets.
/// </summary>
public class ApplicationManifest
{
    [YamlMember(Alias = "application")]
    public ApplicationConfig Application { get; set; } = new();
}

public class ApplicationConfig
{
    /// <summary>Logical name — used as identifier across manifests.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// NuGet version of the package to deploy, e.g. "2.1.0" or "2.1.0-rc1".
    /// Written by <c>installer prepare</c> and used as the default version for all environments.
    /// Can be overridden per-environment in the environment manifest's deployment block.
    /// </summary>
    public string? Version { get; set; }

    public ApplicationType Type { get; set; }

    public PackageConfig Package { get; set; } = new();

    /// <summary>Required when Type = WindowsService.</summary>
    public ServiceConfig? Service { get; set; }

    /// <summary>Required when Type = IisApplication.</summary>
    public IisConfig? Iis { get; set; }

    public ConfigurationConfig Configuration { get; set; } = new();
}

public class PackageConfig
{
    /// <summary>NuGet package ID, e.g. "Company.MyService".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// UNC path or local directory containing .nupkg files.
    /// Can be overridden per-deployment in the environment manifest.
    /// Example: \\fileserver\packages or C:\LocalPackages
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Path within the .nupkg archive where the application content lives.
    /// Defaults to "tools" — all packages in the Elements bundle use this convention.
    /// Note: Chocolatey scripts (chocolateyinstall.ps1 etc.) may also be present under
    /// "tools" but are harmless — the framework never executes them.
    /// </summary>
    [YamlMember(Alias = "content-path")]
    public string ContentPath { get; set; } = "tools";
}

public class ServiceConfig
{
    /// <summary>SCM service name (used with sc.exe / ServiceController).</summary>
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "display-name")]
    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// How the SCM starts the service.
    /// Accepted values (case-insensitive):
    ///   automatic-delayed  – Automatic (Delayed Start)  [default, recommended]
    ///   automatic          – Automatic (starts with OS services, competes at boot)
    ///   manual             – Manual
    ///   disabled           – Disabled
    /// </summary>
    [YamlMember(Alias = "start-type")]
    public string StartType { get; set; } = "automatic-delayed";

    /// <summary>
    /// AD account to run the service as.
    /// When omitted (null), the environment manifest's <c>domain-account.account</c> value
    /// is used.  If that is also unset the built-in fallback <c>LocalSystem</c> is used.
    /// Examples:
    ///   <c>DOMAIN\svc-myapp$</c>  — gMSA (no password needed)
    ///   <c>DOMAIN\svc-myapp</c>   — regular domain account (password required at deploy time)
    ///   <c>LocalSystem</c>        — built-in identity
    /// </summary>
    public string? Account { get; set; }

    /// <summary>Absolute path on target server where the service binary lives.</summary>
    [YamlMember(Alias = "install-directory")]
    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>Optional suffix appended to the binary path in the service definition (e.g. "--environment Production").</summary>
    [YamlMember(Alias = "binary-path-suffix")]
    public string? BinaryPathSuffix { get; set; }
}

public class IisConfig
{
    /// <summary>Name of the IIS site to deploy under. Defaults to "Default Web Site".</summary>
    [YamlMember(Alias = "site-name")]
    public string SiteName { get; set; } = "Default Web Site";

    /// <summary>Virtual path for the IIS application, e.g. "/MyApp".</summary>
    [YamlMember(Alias = "application-path")]
    public string ApplicationPath { get; set; } = string.Empty;

    /// <summary>Absolute physical path on the target server, e.g. "C:\inetpub\apps\MyApp".</summary>
    [YamlMember(Alias = "physical-path")]
    public string PhysicalPath { get; set; } = string.Empty;

    [YamlMember(Alias = "app-pool")]
    public AppPoolConfig AppPool { get; set; } = new();

    /// <summary>
    /// Core = ASP.NET Core (.NET 5+): app pool CLR = "No Managed Code", web.config shipped in package.
    /// Framework = .NET Framework 4.x: app pool CLR = "v4.0".
    /// </summary>
    public DotNetRuntime Runtime { get; set; } = DotNetRuntime.Core;
}

public class AppPoolConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Integrated | Classic</summary>
    [YamlMember(Alias = "pipeline-mode")]
    public string PipelineMode { get; set; } = "Integrated";

    /// <summary>
    /// AD account for the app pool identity.
    /// When omitted (null), the environment manifest's <c>domain-account.account</c> value
    /// is used.  If that is also unset the built-in fallback <c>ApplicationPoolIdentity</c> is used.
    /// Examples:
    ///   <c>DOMAIN\svc-myapp$</c>      — gMSA (no password needed)
    ///   <c>DOMAIN\svc-myapp</c>       — regular domain account (password required at deploy time)
    ///   <c>ApplicationPoolIdentity</c> — built-in virtual identity
    /// </summary>
    public string? Account { get; set; }

    [YamlMember(Alias = "idle-timeout-minutes")]
    public int IdleTimeoutMinutes { get; set; } = 0; // 0 = never recycle due to idle

    [YamlMember(Alias = "auto-start")]
    public bool AutoStart { get; set; } = true;

    [YamlMember(Alias = "start-mode")]
    public string StartMode { get; set; } = "OnDemand"; // OnDemand | AlwaysRunning
}

public enum DotNetRuntime
{
    /// <summary>ASP.NET Core (.NET 5+) — app pool uses "No Managed Code".</summary>
    Core,
    /// <summary>.NET Framework 4.x — app pool uses CLR "v4.0".</summary>
    Framework
}

public class ConfigurationConfig
{
    /// <summary>
    /// Merge mode: the package's own appsettings.json is used as the base; deployment
    /// parameters are overlaid on top.  Keys not listed in parameters keep their package
    /// default values.  This is the recommended and default mode — set explicitly only
    /// to disable it when using overlay or template mode instead.
    /// </summary>
    public bool Merge { get; set; } = false;

    /// <summary>
    /// Template mode: file name within the package containing {{Token}} placeholders.
    /// The merger produces appsettings.json from this template + deployment parameters.
    /// All tokens must be resolved — use Merge mode instead to preserve package defaults.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// Overlay mode: instead of template replacement, generate an appsettings.{Suffix}.json
    /// that ASP.NET Core layered config will merge over the base appsettings.json.
    /// Useful when the package already ships a complete appsettings.json.
    /// </summary>
    [YamlMember(Alias = "overlay-suffix")]
    public string OverlaySuffix { get; set; } = string.Empty; // e.g. "Production" → appsettings.Production.json

    /// <summary>
    /// Application-level parameter defaults, extracted from the package's appsettings.json
    /// by <c>installer prepare</c>.
    ///
    /// These are the starting values for each key.  Any matching key in the environment
    /// manifest's shared-parameters or deployment parameters will override the value here.
    /// Keys present only here — not in the environment manifest — keep their app-manifest
    /// value at deploy time, applied via merge mode against the package's appsettings.json.
    ///
    /// Keys with colons (<c>Section:Key</c>) must be quoted in YAML:
    ///   <c>"AppSettings:ConnectionString": "Server=..."</c>
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>
    /// Legacy: parameters the application REQUIRES.
    /// Validation fails if any are missing from the environment manifest.
    /// Prefer <see cref="Parameters"/> for new manifests — it allows defaults and removes
    /// the requirement to list every key in every environment manifest.
    /// </summary>
    [YamlMember(Alias = "required-parameters")]
    public List<string> RequiredParameters { get; set; } = [];

    /// <summary>Additional template files to process alongside appsettings (e.g. "nlog.config.template").</summary>
    [YamlMember(Alias = "additional-templates")]
    public List<AdditionalTemplate> AdditionalTemplates { get; set; } = [];
}

public class AdditionalTemplate
{
    /// <summary>File name within the package, e.g. "nlog.config.template".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Output file name on disk, e.g. "nlog.config".</summary>
    public string Output { get; set; } = string.Empty;
}
