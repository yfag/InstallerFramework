using InstallerFramework.Core.Configuration;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 3 — Install.
/// Applies environment-specific configuration to the extracted package files in the staging directory.
///
/// Two modes (configured per-application in the manifest):
///   Template mode:  replaces {{Token}} placeholders in appsettings.template.json → appsettings.json
///   Overlay mode:   generates appsettings.{Suffix}.json that ASP.NET Core layered config merges at runtime
///
/// This is the step that eliminates all manual post-install appsettings editing.
/// </summary>
public sealed class ApplyConfigurationStep : IDeploymentStep
{
    private readonly ConfigurationMerger _merger;

    public ApplyConfigurationStep(ConfigurationMerger merger) => _merger = merger;

    public string Name => "Apply Configuration";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.RemoteStagingDirectory is null)
            return StepResult.Fail("Staging directory not set. ExtractPackageStep must run first.");

        var configConfig = context.ApplicationManifest.Application.Configuration;

        // Three-level parameter set (lowest → highest priority):
        //   1. App-manifest defaults  (extracted from package appsettings by 'installer prepare')
        //   2. Environment shared-parameters
        //   3. Deployment-specific parameters
        // This means an environment only needs to list keys that actually differ from the app-manifest defaults.
        var parameters = new Dictionary<string, string>(
            configConfig.Parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.EffectiveParameters)
            parameters[k] = v;

        // Resolve configuration mode:
        //   • explicit merge: true   → merge
        //   • explicit template       → template
        //   • explicit overlay-suffix → overlay
        //   • nothing set             → merge (implicit default; reads package appsettings.json as base)
        var useMerge = configConfig.Merge
            || (string.IsNullOrWhiteSpace(configConfig.Template)
                && string.IsNullOrWhiteSpace(configConfig.OverlaySuffix));

        try
        {
            // --- Primary appsettings ---
            if (useMerge)
            {
                // Merge mode: read appsettings.json from the extracted package, overlay
                // the deployment parameters on top, write back. Keys not in parameters
                // keep their original package values — no manual template editing needed.
                var appsettingsPath = Path.Combine(context.RemoteStagingDirectory, "appsettings.json");

                if (await context.Remote.FileExistsAsync(appsettingsPath, cancellationToken))
                {
                    var baseContent = await context.Remote.ReadFileAsync(appsettingsPath, cancellationToken);
                    var merged = _merger.MergeIntoBase(baseContent, parameters);
                    await context.Remote.WriteFileAsync(appsettingsPath, merged, cancellationToken);
                    context.Logger.LogInformation(
                        "[{App}@{Server}] Merged {Params} parameter(s) into appsettings.json.",
                        context.ApplicationName, context.TargetServer, parameters.Count);
                }
                else
                {
                    // No appsettings.json in the package — write one from scratch
                    context.Logger.LogWarning(
                        "[{App}@{Server}] Merge mode: appsettings.json not found — writing new file from parameters only.",
                        context.ApplicationName, context.TargetServer);
                    var overlay = _merger.BuildOverlay(parameters);
                    await context.Remote.WriteFileAsync(appsettingsPath, overlay, cancellationToken);
                }
            }
            else if (!string.IsNullOrWhiteSpace(configConfig.Template))
            {
                // Template mode: read template from staging dir, replace {{tokens}}, write appsettings.json
                var templatePath = Path.Combine(context.RemoteStagingDirectory, configConfig.Template);
                var templateExists = await context.Remote.FileExistsAsync(templatePath, cancellationToken);

                if (!templateExists)
                    return StepResult.Fail(
                        $"Template file '{configConfig.Template}' not found in package. " +
                        "Ensure the application packages the template, or switch to overlay mode.");

                var templateContent = await context.Remote.ReadFileAsync(templatePath, cancellationToken);
                var merged = _merger.ApplyTemplate(templateContent, parameters);

                var outputPath = Path.Combine(context.RemoteStagingDirectory, "appsettings.json");
                await context.Remote.WriteFileAsync(outputPath, merged, cancellationToken);

                context.Logger.LogInformation(
                    "[{App}@{Server}] Template applied → appsettings.json ({Params} parameters).",
                    context.ApplicationName, context.TargetServer, parameters.Count);
            }
            else if (!string.IsNullOrWhiteSpace(configConfig.OverlaySuffix))
            {
                // Overlay mode: generate appsettings.{Suffix}.json with just the env-specific values
                var overlayContent = _merger.BuildOverlay(parameters);
                var overlayFileName = $"appsettings.{configConfig.OverlaySuffix}.json";
                var outputPath = Path.Combine(context.RemoteStagingDirectory, overlayFileName);
                await context.Remote.WriteFileAsync(outputPath, overlayContent, cancellationToken);

                context.Logger.LogInformation(
                    "[{App}@{Server}] Overlay generated → {File} ({Params} parameters).",
                    context.ApplicationName, context.TargetServer, overlayFileName, parameters.Count);
            }
            else
            {
                context.Logger.LogWarning(
                    "[{App}@{Server}] No configuration template or overlay suffix configured — appsettings will not be modified.",
                    context.ApplicationName, context.TargetServer);
            }

            // --- Additional template files (e.g. nlog.config.template) ---
            foreach (var additionalTemplate in configConfig.AdditionalTemplates)
            {
                var srcPath = Path.Combine(context.RemoteStagingDirectory, additionalTemplate.Source);
                var dstPath = Path.Combine(context.RemoteStagingDirectory, additionalTemplate.Output);

                if (!await context.Remote.FileExistsAsync(srcPath, cancellationToken))
                {
                    context.Logger.LogWarning(
                        "[{App}@{Server}] Additional template '{Src}' not found — skipping.",
                        context.ApplicationName, context.TargetServer, additionalTemplate.Source);
                    continue;
                }

                var templateContent = await context.Remote.ReadFileAsync(srcPath, cancellationToken);
                var merged = _merger.ApplyTemplate(templateContent, parameters);
                await context.Remote.WriteFileAsync(dstPath, merged, cancellationToken);

                context.Logger.LogInformation(
                    "[{App}@{Server}] Additional template applied: {Src} → {Dst}",
                    context.ApplicationName, context.TargetServer, additionalTemplate.Source, additionalTemplate.Output);
            }

            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Configuration merge failed: {ex.Message}");
        }
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Staging dir rollback is handled by ExtractPackageStep.
}
