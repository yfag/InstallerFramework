using InstallerFramework.Core.Models;

namespace InstallerFramework.Core.Configuration;

/// <summary>
/// Cross-manifest validation: checks that an environment manifest and its application manifests
/// are consistent with each other.
/// Run this before any deployment — it's the "fail fast before touching servers" gate.
/// </summary>
public sealed class ManifestValidator
{
    /// <summary>
    /// Validates all deployments in the environment manifest against their application manifests.
    /// Returns a list of validation errors (empty = valid).
    /// </summary>
    public IReadOnlyList<string> Validate(
        EnvironmentManifest environmentManifest,
        IReadOnlyDictionary<string, ApplicationManifest> applicationManifests)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(environmentManifest.Environment.Name))
            errors.Add("Environment name is not set.");

        foreach (var deployment in environmentManifest.Environment.Deployments)
        {
            var prefix = $"[{deployment.Application}]";

            if (!applicationManifests.TryGetValue(deployment.Application, out var appManifest))
            {
                errors.Add($"{prefix} No application manifest found for '{deployment.Application}'.");
                continue;
            }

            var app = appManifest.Application;

            // Version — acceptable from either the deployment block or the application manifest
            var effectiveVersion = !string.IsNullOrWhiteSpace(deployment.Version)
                ? deployment.Version
                : appManifest.Application.Version;
            if (string.IsNullOrWhiteSpace(effectiveVersion))
                errors.Add($"{prefix} No version specified. Set 'version' in the application manifest or in the environment deployment block.");

            // Servers
            if (deployment.Servers.Count == 0)
                errors.Add($"{prefix} No target servers specified.");

            if (deployment.Servers.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{prefix} One or more server names are empty.");

            // Required parameters — satisfied by:
            //   1. App-manifest defaults  (configuration.parameters)
            //   2. Environment shared-parameters
            //   3. Deployment-specific parameters
            // Keys present in configuration.parameters have a default value and do not
            // need to be listed in the environment manifest.
            var availableParams = new HashSet<string>(
                app.Configuration.Parameters.Keys,
                StringComparer.OrdinalIgnoreCase);
            availableParams.UnionWith(environmentManifest.Environment.SharedParameters.Keys);
            availableParams.UnionWith(deployment.Parameters.Keys);

            var missing = app.Configuration.RequiredParameters
                .Where(p => !availableParams.Contains(p))
                .ToList();

            if (missing.Count > 0)
                errors.Add($"{prefix} Missing required parameters: {string.Join(", ", missing)}");

            // Type-specific validation
            if (app.Type == ApplicationType.WindowsService)
                ValidateServiceConfig(app, deployment, prefix, errors);

            if (app.Type == ApplicationType.IisApplication)
                ValidateIisConfig(app, deployment, prefix, errors);

            // Configuration
            var config = app.Configuration;
            if (!string.IsNullOrWhiteSpace(config.Template) && !string.IsNullOrWhiteSpace(config.OverlaySuffix))
                errors.Add($"{prefix} Both 'template' and 'overlay-suffix' are set — use one or the other.");
        }

        return errors.AsReadOnly();
    }

    private static void ValidateServiceConfig(
        ApplicationConfig app, ApplicationDeployment deployment, string prefix, List<string> errors)
    {
        var svc = app.Service;
        if (svc is null) { errors.Add($"{prefix} Type is WindowsService but no 'service' block defined."); return; }

        if (string.IsNullOrWhiteSpace(svc.Name))
            errors.Add($"{prefix} Service 'name' is not set.");
        if (string.IsNullOrWhiteSpace(svc.InstallDirectory))
            errors.Add($"{prefix} Service 'install-directory' is not set.");

        var validStartTypes = new[] { "automatic-delayed", "automatic", "manual", "disabled" };
        if (!validStartTypes.Contains(svc.StartType.ToLowerInvariant()))
            errors.Add($"{prefix} Service 'start-type' must be one of: {string.Join(", ", validStartTypes)}.");
    }

    private static void ValidateIisConfig(
        ApplicationConfig app, ApplicationDeployment deployment, string prefix, List<string> errors)
    {
        var iis = app.Iis;
        if (iis is null) { errors.Add($"{prefix} Type is IisApplication but no 'iis' block defined."); return; }

        if (string.IsNullOrWhiteSpace(iis.SiteName))
            errors.Add($"{prefix} IIS 'site-name' is not set.");
        if (string.IsNullOrWhiteSpace(iis.ApplicationPath))
            errors.Add($"{prefix} IIS 'application-path' is not set.");
        if (string.IsNullOrWhiteSpace(iis.PhysicalPath))
            errors.Add($"{prefix} IIS 'physical-path' is not set.");
        if (string.IsNullOrWhiteSpace(iis.AppPool.Name))
            errors.Add($"{prefix} IIS app pool 'name' is not set.");

        if (!iis.ApplicationPath.StartsWith('/'))
            errors.Add($"{prefix} IIS 'application-path' must start with '/' (e.g. '/MyApp').");
    }
}
