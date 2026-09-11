using System.IO.Compression;
using System.Text;
using System.Text.Json;
using InstallerFramework.Core.Packages;

namespace InstallerFramework.Core.Preparation;

/// <summary>
/// Generates a draft .application.yaml by inspecting the contents of a .nupkg file.
///
/// Detection logic — primary signal:
///   tools/Helpers.Web.ps1 present      → IIS application
///   tools/Helpers.Services.ps1 present → Windows Service
///
///   These helper scripts are injected by the Elements build pipeline and are
///   100% authoritative. They are far more reliable than trying to infer type
///   from web.config presence (modern .NET services publish a web.config even
///   when self-hosted, so web.config is not a reliable discriminator).
///
/// Fallback (non-standard / legacy packages with neither helper):
///   web.config found in tools/ or inside inner ZIP → IIS application
///   otherwise                                      → Windows Service
///
/// Package layouts handled:
///   Single-nested: application files directly under tools/
///   Double-nested (WebDeploy / service ZIPs): a .zip inside tools/ holds the
///   actual content. For IIS packages this is a WebDeploy archive with
///   Content/D_C/a/... paths; for Windows Services it's a flat ZIP with a
///   single-folder prefix (e.g. DocumentDeliveryService\...).
///
/// Configuration keys for IIS apps:  extracted from tools/appsettings.json
/// Configuration keys for WS apps:   extracted from appsettings.json inside
///                                    the inner ZIP
/// </summary>
public sealed class ManifestGenerator
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a draft manifest for one .nupkg file.
    /// Returns null if the package cannot be read or has no usable content.
    /// </summary>
    public GeneratedManifest? GenerateFromPackage(string nupkgPath)
    {
        if (!File.Exists(nupkgPath))
            throw new FileNotFoundException($"Package not found: {nupkgPath}");

        var fileName = Path.GetFileNameWithoutExtension(nupkgPath);
        var parsed   = NuGetPackageResolver.ParsePackageFileName(fileName);
        if (parsed is null)
            return null;

        var (packageId, version) = parsed.Value;

        using var zip = ZipFile.OpenRead(nupkgPath);
        var info = InspectNupkg(zip, packageId);

        if (info.HasWebConfig)
        {
            return new GeneratedManifest
            {
                PackageId        = packageId,
                Version          = version,
                SourceFileName   = Path.GetFileName(nupkgPath),
                AppType          = GeneratedAppType.IisApplication,
                Runtime          = info.IsAspNetCore ? GeneratedRuntime.Core : GeneratedRuntime.Framework,
                AppName          = DeriveAppName(packageId),
                ConfigKeyValues  = info.ConfigKeyValues,
                HasAppSettings   = info.HasAppSettings
            };
        }
        else
        {
            return new GeneratedManifest
            {
                PackageId        = packageId,
                Version          = version,
                SourceFileName   = Path.GetFileName(nupkgPath),
                AppType          = GeneratedAppType.WindowsService,
                ServiceExeName   = info.ServiceExeName,
                ConfigKeyValues  = info.ConfigKeyValues,
                HasAppSettings   = info.HasAppSettings
            };
        }
    }

    /// <summary>
    /// Reads just the <c>version:</c> field from an existing .application.yaml without
    /// fully deserialising the file.  Returns null if the file cannot be read or has no
    /// version field.  Used by <c>installer prepare</c> to decide whether to regenerate.
    /// </summary>
    public static string? ReadVersionFromManifest(string manifestPath)
    {
        try
        {
            foreach (var line in File.ReadLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["version:".Length..].Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the <c>runtime:</c> field from an existing .application.yaml without fully
    /// deserialising the file.  Returns null if the file cannot be read, has no runtime
    /// field, or contains an unrecognised value.
    /// Used by <c>installer prepare</c> to preserve manually corrected runtime settings
    /// across version updates.
    /// </summary>
    public static GeneratedRuntime? ReadRuntimeFromManifest(string manifestPath)
    {
        try
        {
            foreach (var line in File.ReadLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("runtime:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = trimmed["runtime:".Length..].Trim();
                // Strip inline comments (e.g. "Framework    # Detected: ...")
                var commentIdx = value.IndexOf('#');
                if (commentIdx >= 0) value = value[..commentIdx].Trim();

                if (value.Equals("Framework", StringComparison.OrdinalIgnoreCase))
                    return GeneratedRuntime.Framework;
                if (value.Equals("Core", StringComparison.OrdinalIgnoreCase))
                    return GeneratedRuntime.Core;
                return null; // Unrecognised value
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Renders a deployment-template.yaml containing one deployment stub per manifest.
    /// The file is intended to be copy-pasted into an environment manifest's
    /// <c>deployments:</c> section, then edited to add server names and any
    /// environment-specific parameter overrides.
    ///
    /// Parameters that have an empty default value in the package are highlighted
    /// with a comment — these almost certainly need an environment-specific value.
    /// All other parameter overrides are shown as commented-out examples.
    /// </summary>
    public string RenderDeploymentTemplate(IEnumerable<GeneratedManifest> manifests)
    {
        var sb   = new StringBuilder();
        var date = DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine("# deployment-template.yaml");
        sb.AppendLine($"# Generated by 'installer prepare' on {date}");
        sb.AppendLine("#");
        sb.AppendLine("# Copy the entries below into your <environment>.yaml 'deployments:' section.");
        sb.AppendLine("# For each application:");
        sb.AppendLine("#   1. Add the target servers (replace the commented-out examples).");
        sb.AppendLine("#   2. Fill in any parameters marked '(no default)' — these are required.");
        sb.AppendLine("#   3. Override other parameters that differ from the app-manifest defaults.");
        sb.AppendLine("#   4. Adjust health-check URLs to match your environment and server names.");
        sb.AppendLine("#");
        sb.AppendLine("# The 'version:' field is optional — each application manifest already");
        sb.AppendLine("# contains the version detected from the package. Only add it here to pin");
        sb.AppendLine("# a different version for this specific environment.");
        sb.AppendLine();
        sb.AppendLine("deployments:");

        foreach (var manifest in manifests)
            RenderDeploymentEntry(sb, manifest);

        return sb.ToString();
    }

    private static void RenderDeploymentEntry(StringBuilder sb, GeneratedManifest manifest)
    {
        var appName = manifest.PackageId;
        var isIis   = manifest.AppType == GeneratedAppType.IisApplication;

        sb.AppendLine();
        sb.AppendLine($"  - application: {appName}");
        sb.AppendLine($"    # version: {manifest.Version}   # optional — overrides the application manifest default");
        sb.AppendLine($"    servers:");
        sb.AppendLine($"      # - SERVER-A");
        sb.AppendLine($"      # - SERVER-B");

        // Parameters section
        sb.AppendLine($"    parameters:");
        sb.AppendLine($"      # Parameters with no default value — must be set before deploying:");
        sb.AppendLine($"      # Override other app-manifest defaults as needed, e.g.:");
        sb.AppendLine($"      # \"AppSettings:SomeKey\": \"environment-specific-value\"");
        sb.AppendLine($"      # See {manifest.PackageId}.application.yaml for all available keys and their default values.");

        // Health-check section
        if (isIis)
        {
            sb.AppendLine($"    health-check:");
            sb.AppendLine($"      url: \"https://SERVER-A/{appName}/api/version\"");
            sb.AppendLine($"      timeout-seconds: 30");
            sb.AppendLine($"      retry-count: 5");
            sb.AppendLine($"      retry-interval-seconds: 5");
            sb.AppendLine($"      expected-status: \"200\"");
        }
        else
        {
            sb.AppendLine($"    # health-check:   # Windows Services typically don't expose HTTP health endpoints");
        }
    }

    /// <summary>
    /// Renders the draft .application.yaml content for a generated manifest.
    /// The <paramref name="account"/> parameter is accepted for backward compatibility
    /// but is no longer written into the manifest — set the account in your environment
    /// manifest's <c>domain-account.account</c> instead.
    /// </summary>
    public string RenderApplicationYaml(GeneratedManifest manifest, string? account = null)
    {
        var sb      = new StringBuilder();
        var appName = manifest.PackageId;
        var svcName = manifest.PackageId;
        var date    = DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine($"# {manifest.PackageId}.application.yaml");
        sb.AppendLine($"# Generated by 'installer prepare' on {date}");
        sb.AppendLine($"# Source package: {manifest.SourceFileName}");
        sb.AppendLine($"# Detected type:  {(manifest.AppType == GeneratedAppType.IisApplication ? $"IIS application ({manifest.Runtime})" : "Windows Service")}");
        sb.AppendLine($"#");
        sb.AppendLine($"# Service account is configured at the environment level (domain-account.account in your");
        sb.AppendLine($"# environment manifest).  Add an 'account:' field here only to override it for this app.");
        sb.AppendLine($"# Parameters listed below are defaults from the package's appsettings.json.");
        sb.AppendLine($"# Override any of them in your environment manifest's parameters section.");
        sb.AppendLine();
        sb.AppendLine("application:");
        sb.AppendLine($"  name: {manifest.PackageId}");
        sb.AppendLine($"  version: {manifest.Version}");
        sb.AppendLine($"  type: {(manifest.AppType == GeneratedAppType.IisApplication ? "iis-application" : "windows-service")}");
        sb.AppendLine();
        sb.AppendLine("  package:");
        sb.AppendLine($"    id: {manifest.PackageId}");
        sb.AppendLine($"    # content-path defaults to \"tools\"");
        sb.AppendLine();

        if (manifest.AppType == GeneratedAppType.WindowsService)
            RenderServiceSection(sb, svcName);
        else
            RenderIisSection(sb, appName, manifest.Runtime);

        sb.AppendLine();
        RenderConfigurationSection(sb, manifest);

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Package inspection
    // -------------------------------------------------------------------------

    private sealed record PackageInspection(
        bool HasWebConfig,
        bool IsAspNetCore,
        bool HasAppSettings,
        Dictionary<string, string> ConfigKeyValues,
        string? ServiceExeName);

    /// <summary>
    /// Determines application type and extracts config keys from a nupkg archive.
    ///
    /// Decision tree:
    ///   1. Helpers.Web.ps1 present     → IIS (authoritative)
    ///   2. Helpers.Services.ps1 present → Windows Service (authoritative)
    ///   3. Neither present              → fallback to web.config detection
    /// </summary>
    private static PackageInspection InspectNupkg(ZipArchive nupkg, string packageId)
    {
        bool hasWebHelper = nupkg.Entries.Any(e =>
            e.Name.Equals("Helpers.Web.ps1", StringComparison.OrdinalIgnoreCase));
        bool hasServicesHelper = nupkg.Entries.Any(e =>
            e.Name.Equals("Helpers.Services.ps1", StringComparison.OrdinalIgnoreCase));

        var directAppSettings = FindEntry(nupkg, "tools/appsettings.json");

        if (hasWebHelper)
            return InspectIisPackage(nupkg, directAppSettings);

        if (hasServicesHelper)
            return InspectWindowsServicePackage(nupkg, packageId, directAppSettings);

        return FallbackInspect(nupkg, packageId, directAppSettings);
    }

    private static PackageInspection InspectIisPackage(
        ZipArchive nupkg, ZipArchiveEntry? directAppSettings)
    {
        Dictionary<string, string> keyValues;
        bool hasAppSettings;

        if (directAppSettings is not null)
        {
            keyValues = ExtractJsonKeyValuesFromEntry(directAppSettings);
            hasAppSettings = true;
        }
        else
        {
            var innerZipEntry = FindInnerZipEntry(nupkg);
            if (innerZipEntry is not null)
            {
                using var ms = new MemoryStream();
                using (var s = innerZipEntry.Open()) s.CopyTo(ms);
                ms.Position = 0;
                using var inner = new ZipArchive(ms, ZipArchiveMode.Read);
                var innerAppSettings = FindEntryByName(inner, "appsettings.json");
                keyValues = innerAppSettings is not null
                    ? ExtractJsonKeyValuesFromEntry(innerAppSettings)
                    : [];
                hasAppSettings = innerAppSettings is not null;
            }
            else
            {
                keyValues = [];
                hasAppSettings = false;
            }
        }

        var isCore = DetectCoreRuntime(nupkg);

        return new PackageInspection(
            HasWebConfig:    true,
            IsAspNetCore:    isCore,
            HasAppSettings:  hasAppSettings,
            ConfigKeyValues: keyValues,
            ServiceExeName:  null);
    }

    private static PackageInspection InspectWindowsServicePackage(
        ZipArchive nupkg, string packageId, ZipArchiveEntry? directAppSettings)
    {
        var innerZipEntry = FindInnerZipEntry(nupkg);

        if (innerZipEntry is not null)
        {
            using var ms = new MemoryStream();
            using (var s = innerZipEntry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            using var inner = new ZipArchive(ms, ZipArchiveMode.Read);

            var innerAppSettings = FindEntryByName(inner, "appsettings.json");
            var keyValues = innerAppSettings is not null
                ? ExtractJsonKeyValuesFromEntry(innerAppSettings)
                : (directAppSettings is not null ? ExtractJsonKeyValuesFromEntry(directAppSettings) : []);

            return new PackageInspection(
                HasWebConfig:    false,
                IsAspNetCore:    false,
                HasAppSettings:  innerAppSettings is not null || directAppSettings is not null,
                ConfigKeyValues: keyValues,
                ServiceExeName:  FindServiceExeNameInner(inner, packageId));
        }

        var directKeyValues = directAppSettings is not null
            ? ExtractJsonKeyValuesFromEntry(directAppSettings)
            : new Dictionary<string, string>();
        return new PackageInspection(
            HasWebConfig:    false,
            IsAspNetCore:    false,
            HasAppSettings:  directAppSettings is not null,
            ConfigKeyValues: directKeyValues,
            ServiceExeName:  FindServiceExeNameDirect(nupkg, packageId));
    }

    private static PackageInspection FallbackInspect(
        ZipArchive nupkg, string packageId, ZipArchiveEntry? directAppSettings)
    {
        var directWebConfig = FindEntry(nupkg, "tools/web.config");
        if (directWebConfig is not null || directAppSettings is not null)
        {
            var kv     = directAppSettings is not null ? ExtractJsonKeyValuesFromEntry(directAppSettings) : [];
            var isCore = directWebConfig is not null && DetectCore(directWebConfig);
            var exe    = directWebConfig is null ? FindServiceExeNameDirect(nupkg, packageId) : null;
            return new PackageInspection(directWebConfig is not null, isCore, directAppSettings is not null, kv, exe);
        }

        var innerZipEntry = FindInnerZipEntry(nupkg);
        if (innerZipEntry is not null)
        {
            using var ms = new MemoryStream();
            using (var s = innerZipEntry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            using var inner = new ZipArchive(ms, ZipArchiveMode.Read);

            var innerWebConfig   = FindEntryByName(inner, "web.config");
            var innerAppSettings = FindEntryByName(inner, "appsettings.json");
            var kv     = innerAppSettings is not null ? ExtractJsonKeyValuesFromEntry(innerAppSettings) : [];
            var isCore = innerWebConfig is not null && DetectCore(innerWebConfig);
            var exe    = innerWebConfig is null ? FindServiceExeNameInner(inner, packageId) : null;
            return new PackageInspection(innerWebConfig is not null, isCore, innerAppSettings is not null, kv, exe);
        }

        return new PackageInspection(false, false, false, [], null);
    }

    // -------------------------------------------------------------------------
    // Private helpers — runtime detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Detects Core vs Framework for an IIS package.
    /// Returns false (Framework) when detection is inconclusive — .NET Framework is the
    /// safer default for this organisation's legacy-heavy application estate.
    /// </summary>
    private static bool DetectCoreRuntime(ZipArchive nupkg)
    {
        var directWebConfig = FindEntry(nupkg, "tools/web.config");
        if (directWebConfig is not null)
            return DetectCore(directWebConfig);

        var innerZipEntry = FindInnerZipEntry(nupkg);
        if (innerZipEntry is null) return false; // default: Framework

        using var ms = new MemoryStream();
        using (var s = innerZipEntry.Open()) s.CopyTo(ms);
        ms.Position = 0;
        using var inner = new ZipArchive(ms, ZipArchiveMode.Read);
        var webConfig = FindEntryByName(inner, "web.config");
        return webConfig is not null && DetectCore(webConfig);
    }

    private static bool DetectCore(ZipArchiveEntry webConfigEntry)
    {
        try
        {
            using var stream = webConfigEntry.Open();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            // <system.web> is a definitive .NET Framework indicator — always present in
            // Framework web.configs (system.web is a Framework-only config section),
            // and never present in ASP.NET Core web.configs.
            // Check this first because some hybrid apps may reference aspNetCore packages
            // while still running on the Framework pipeline.
            if (content.Contains("<system.web", StringComparison.OrdinalIgnoreCase))
                return false; // Framework

            // aspNetCoreModuleV2 handler registration is the standard ASP.NET Core indicator.
            return content.Contains("aspNetCore", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // default: Framework
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — ZIP entry lookup
    // -------------------------------------------------------------------------

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            e.FullName.Equals(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

    private static ZipArchiveEntry? FindEntryByName(ZipArchive zip, string fileName) =>
        zip.Entries
           .Where(e => e.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
           .OrderBy(e => e.FullName.Count(c => c == '/' || c == '\\'))
           .FirstOrDefault();

    private static ZipArchiveEntry? FindInnerZipEntry(ZipArchive nupkg) =>
        nupkg.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------------------
    // Private helpers — EXE detection
    // -------------------------------------------------------------------------

    private static string? FindServiceExeNameDirect(ZipArchive nupkg, string packageId)
    {
        var exeEntries = nupkg.Entries
            .Where(e =>
                e.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) &&
                e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !IsChocolateyHelper(e.Name))
            .ToList();
        return PickBestExe(exeEntries, packageId);
    }

    private static string? FindServiceExeNameInner(ZipArchive inner, string packageId)
    {
        var exeEntries = inner.Entries
            .Where(e =>
                e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !IsChocolateyHelper(e.Name))
            .ToList();
        return PickBestExe(exeEntries, packageId);
    }

    private static string? PickBestExe(IList<ZipArchiveEntry> exeEntries, string packageId)
    {
        if (exeEntries.Count == 0) return null;
        return exeEntries
            .OrderByDescending(e =>
                Path.GetFileNameWithoutExtension(e.Name)
                    .Equals(packageId, StringComparison.OrdinalIgnoreCase) ? 2 :
                packageId.Contains(Path.GetFileNameWithoutExtension(e.Name),
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(e => Path.GetFileNameWithoutExtension(e.Name))
            .First();
    }

    private static bool IsChocolateyHelper(string fileName) =>
        fileName.StartsWith("chocolatey", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("shimgen.exe", StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Private helpers — JSON key-value extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts all scalar leaf values from a JSON file as a flat colon-separated
    /// key-value dictionary (e.g. "AppSettings:ConnectionString" → "Server=…").
    /// Arrays are skipped — they cannot be represented as simple key:value pairs.
    /// </summary>
    private static Dictionary<string, string> ExtractJsonKeyValuesFromEntry(ZipArchiveEntry entry)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = entry.Open();
            using var doc    = JsonDocument.Parse(stream);
            FlattenElement(doc.RootElement, null, result);
        }
        catch { /* Malformed appsettings.json — return empty */ }
        return result;
    }

    private static void FlattenElement(JsonElement element, string? prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var childKey = prefix is null ? prop.Name : $"{prefix}:{prop.Name}";
                    FlattenElement(prop.Value, childKey, result);
                }
                break;

            case JsonValueKind.Array:
                break; // Skip — not representable as simple key:value pairs

            default:
                if (prefix is not null)
                {
                    var value = element.ValueKind switch
                    {
                        JsonValueKind.Null   => "",
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        JsonValueKind.Number => element.GetRawText(),
                        JsonValueKind.String => element.GetString() ?? "",
                        _                    => element.GetRawText()
                    };
                    result[prefix] = value;
                }
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — YAML rendering
    // -------------------------------------------------------------------------

    private static void RenderServiceSection(StringBuilder sb, string serviceName)
    {
        sb.AppendLine("  service:");
        sb.AppendLine($"    name: {serviceName}");
        sb.AppendLine($"    display-name: \"{serviceName}\"");
        sb.AppendLine($"    description: \"\"");
        sb.AppendLine($"    start-type: automatic-delayed");
        sb.AppendLine($"    # account:                    # optional: overrides environment default (domain-account.account)");
        sb.AppendLine($@"    install-directory: C:\Program Files\{serviceName}");
    }

    private static void RenderIisSection(StringBuilder sb, string appName, GeneratedRuntime runtime)
    {
        var runtimeStr  = runtime == GeneratedRuntime.Core ? "Core" : "Framework";
        var runtimeNote = runtime == GeneratedRuntime.Core
            ? "Detected: ASP.NET Core — app pool uses No Managed Code"
            : "Detected: .NET Framework — app pool uses CLR v4.0";

        sb.AppendLine("  iis:");
        sb.AppendLine($"    site-name: Default Web Site");
        sb.AppendLine($"    application-path: /{appName}");
        sb.AppendLine($@"    physical-path: C:\inetpub\wwwroot\{appName}");
        sb.AppendLine($"    runtime: {runtimeStr,-8}                     # {runtimeNote}");
        sb.AppendLine($"    app-pool:");
        sb.AppendLine($"      name: {appName}");
        sb.AppendLine($"      pipeline-mode: Integrated");
        sb.AppendLine($"      # account:                  # optional: overrides environment default (domain-account.account)");
        sb.AppendLine($"      idle-timeout-minutes: 0");
        sb.AppendLine($"      auto-start: true");
        sb.AppendLine($"      start-mode: AlwaysRunning");
    }

    private static void RenderConfigurationSection(StringBuilder sb, GeneratedManifest manifest)
    {
        sb.AppendLine("  configuration:");

        if (manifest.ConfigKeyValues.Count == 0)
        {
            sb.AppendLine("    # No appsettings.json found — add parameters manually if needed.");
            sb.AppendLine("    parameters: {}");
            return;
        }

        sb.AppendLine("    # Default values extracted from the package's appsettings.json.");
        sb.AppendLine("    # Override any of these in your environment manifest's parameters section.");
        sb.AppendLine("    # Keys not overridden keep these values at deploy time.");
        sb.AppendLine("    parameters:");

        foreach (var (key, value) in manifest.ConfigKeyValues)
        {
            // YAML mapping keys that contain ':' must be quoted; values are always quoted
            // to handle connection strings, URLs, and other special-character content.
            var yamlKey   = key.Contains(':') ? $"\"{key}\"" : key;
            var yamlValue = YamlQuote(value);
            sb.AppendLine($"      {yamlKey}: {yamlValue}");
        }
    }

    /// <summary>
    /// Returns a YAML-safe double-quoted scalar.
    /// Empty strings render as two empty double-quotes.
    /// </summary>
    private static string YamlQuote(string value)
    {
        // Escape backslashes and double-quotes inside the quoted scalar.
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string DeriveAppName(string packageId) => packageId;
}

// -------------------------------------------------------------------------
// Result model
// -------------------------------------------------------------------------

public sealed class GeneratedManifest
{
    public required string PackageId        { get; init; }
    public required string Version          { get; init; }
    public required string SourceFileName   { get; init; }
    public required GeneratedAppType AppType { get; init; }
    public GeneratedRuntime Runtime         { get; set; } = GeneratedRuntime.Framework;
    public string? AppName                  { get; init; }
    public string? ServiceExeName           { get; init; }
    public Dictionary<string, string> ConfigKeyValues { get; init; } = [];
    public bool HasAppSettings              { get; init; }

    /// <summary>Number of configuration keys found in the package's appsettings.json.</summary>
    public int KeyCount => ConfigKeyValues.Count;
}

public enum GeneratedAppType  { WindowsService, IisApplication }
public enum GeneratedRuntime  { Core, Framework }
