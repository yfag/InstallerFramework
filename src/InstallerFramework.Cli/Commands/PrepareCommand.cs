using System.CommandLine;
using InstallerFramework.Core.Packages;
using InstallerFramework.Core.Preparation;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

/// <summary>
/// installer prepare --source .\ChocoPackages
/// installer prepare --source .\ChocoPackages --out .\custom-output
///
/// Scans a package directory and generates a draft .application.yaml for each package found.
/// Configuration keys are extracted from the package's appsettings.json and written as
/// 'parameters:' defaults — no manual editing needed for keys that don't vary by environment.
/// </summary>
public static class PrepareCommand
{
    public static Command Create()
    {
        var sourceOption = new Option<DirectoryInfo>(
            aliases: ["--source", "-s"],
            description: "Path to the ChocoPackages directory containing .nupkg files.")
        { IsRequired = true };

        var packageOption = new Option<string?>(
            aliases: ["--package", "-p"],
            description: "Generate for this package ID only. Omit to process all packages in the source.");

        var outOption = new Option<DirectoryInfo?>(
            aliases: ["--out", "-o"],
            description: "Output directory for generated manifest files. " +
                         "Defaults to a 'manifests' folder alongside the ChocoPackages directory " +
                         @"(i.e. ..\manifests relative to --source).")
        { IsRequired = false };

        var overwriteOption = new Option<bool>(
            aliases: ["--overwrite"],
            description: "Overwrite existing manifest files. By default, existing files are skipped.");

        var command = new Command(
            "prepare",
            "Generate draft application manifests by inspecting .nupkg contents.")
        {
            sourceOption, packageOption, outOption, overwriteOption
        };

        command.SetHandler((source, packageFilter, outDirArg, overwrite) =>
        {
            // Default output: a 'manifests' folder alongside ChocoPackages inside the bundle.
            // e.g. bundle\ChocoPackages → bundle\manifests
            var outDir = outDirArg
                ?? new DirectoryInfo(
                    Path.Combine(
                        source.Parent?.FullName ?? source.FullName,
                        "manifests"));

            AnsiConsole.MarkupLine("[bold]InstallerFramework[/] — prepare");
            AnsiConsole.MarkupLine($"Source:  [cyan]{Markup.Escape(source.FullName)}[/]");
            AnsiConsole.MarkupLine($"Output:  [cyan]{Markup.Escape(outDir.FullName)}[/]");
            if (overwrite)
                AnsiConsole.MarkupLine("[yellow]--overwrite: existing manifests will be replaced[/]");

            if (!source.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Source directory not found: {Markup.Escape(source.FullName)}");
                Environment.Exit(1);
                return;
            }

            outDir.Create(); // Ensure output directory exists

            // Find packages to process
            var allPackages = source.GetFiles("*.nupkg", SearchOption.TopDirectoryOnly);
            if (allPackages.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No .nupkg files found in source directory.[/]");
                return;
            }

            var packagesToProcess = packageFilter is null
                ? allPackages
                : allPackages.Where(f =>
                {
                    var parsed = NuGetPackageResolver.ParsePackageFileName(
                        Path.GetFileNameWithoutExtension(f.Name));
                    return parsed is not null &&
                           parsed.Value.PackageId.Equals(packageFilter, StringComparison.OrdinalIgnoreCase);
                }).ToArray();

            if (packagesToProcess.Length == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No packages found matching '{Markup.Escape(packageFilter ?? "")}'.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"Processing [bold]{packagesToProcess.Length}[/] package(s)...\n");

            var generator         = new ManifestGenerator();
            int newCount = 0, updatedCount = 0, skipped = 0, failed = 0;
            var generatedManifests = new List<GeneratedManifest>();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Package")
                .AddColumn("Type")
                .AddColumn("Keys found")
                .AddColumn("Result");

            foreach (var package in packagesToProcess.OrderBy(f => f.Name))
            {
                var parsed = NuGetPackageResolver.ParsePackageFileName(
                    Path.GetFileNameWithoutExtension(package.Name));
                if (parsed is null) continue;

                var packageId      = parsed.Value.PackageId;
                var packageVersion = parsed.Value.Version;
                var manifestFile   = Path.Combine(outDir.FullName, $"{packageId}.application.yaml");

                // Read the version and runtime from an existing manifest (if any).
                // Version determines whether to skip, update, or generate fresh.
                // Runtime is preserved on version updates so that any manual correction
                // (e.g. changing Core → Framework for a hybrid app) survives re-prepare
                // without needing --overwrite.
                var existingManifestExists = File.Exists(manifestFile);
                var existingVersion = existingManifestExists
                    ? ManifestGenerator.ReadVersionFromManifest(manifestFile)
                    : null;
                // Only read existing runtime when we'll be doing an update (not --overwrite,
                // not a brand-new manifest) — for new generates and forced overwrites we
                // always use fresh detection.
                var existingRuntime = (!overwrite && existingManifestExists)
                    ? ManifestGenerator.ReadRuntimeFromManifest(manifestFile)
                    : null;

                if (!overwrite && existingVersion is not null &&
                    existingVersion.Equals(packageVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // Same version — nothing to do.
                    table.AddRow(
                        Markup.Escape(packageId),
                        "[dim]—[/]",
                        "[dim]—[/]",
                        $"[dim]skipped (v{Markup.Escape(existingVersion)} unchanged)[/]");
                    skipped++;
                    continue;
                }

                try
                {
                    var manifest = generator.GenerateFromPackage(package.FullName);
                    if (manifest is null)
                    {
                        table.AddRow(
                            Markup.Escape(packageId),
                            "[dim]—[/]",
                            "[dim]—[/]",
                            "[yellow]skipped (could not parse)[/]");
                        skipped++;
                        continue;
                    }

                    // Preserve a manually corrected runtime setting from the existing manifest.
                    // Only applies to IIS apps (Windows Services don't have a runtime field).
                    // --overwrite intentionally bypasses this to allow fresh detection.
                    if (existingRuntime.HasValue && manifest.AppType == GeneratedAppType.IisApplication)
                        manifest.Runtime = existingRuntime.Value;

                    // Write .application.yaml (account parameter omitted — comes from environment manifest)
                    var yaml = generator.RenderApplicationYaml(manifest);
                    File.WriteAllText(manifestFile, yaml);
                    generatedManifests.Add(manifest);

                    var typeLabel = manifest.AppType == GeneratedAppType.IisApplication
                        ? $"IIS ({manifest.Runtime})"
                        : "Windows Service";

                    var keyCount = manifest.KeyCount > 0
                        ? manifest.KeyCount.ToString()
                        : "[yellow]0 (no appsettings.json)[/]";

                    string resultLabel;
                    if (existingVersion is null)
                    {
                        resultLabel = $"[green]✓ generated (v{Markup.Escape(manifest.Version)})[/]";
                        newCount++;
                    }
                    else
                    {
                        resultLabel = $"[green]✓ updated v{Markup.Escape(existingVersion)} → v{Markup.Escape(manifest.Version)}[/]";
                        updatedCount++;
                    }

                    table.AddRow(Markup.Escape(packageId), Markup.Escape(typeLabel), keyCount, resultLabel);
                }
                catch (Exception ex)
                {
                    table.AddRow(
                        Markup.Escape(packageId),
                        "[dim]—[/]",
                        "[dim]—[/]",
                        $"[red]✗ {Markup.Escape(ex.Message)}[/]");
                    failed++;
                }
            }

            var totalWritten = newCount + updatedCount;
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                (newCount     > 0 ? $"[green]{newCount} new[/]"             : "") +
                (updatedCount > 0 ? (newCount > 0 ? ", " : "") + $"[cyan]{updatedCount} updated[/]" : "") +
                (skipped      > 0 ? (newCount + updatedCount > 0 ? ", " : "") + $"[dim]{skipped} unchanged[/]" : "") +
                (failed       > 0 ? $", [red]{failed} failed[/]"            : "") +
                $" → [cyan]{Markup.Escape(outDir.FullName)}[/]");

            // Write deployment-template.yaml only when processing the full bundle (no --package filter).
            // Single-package regeneration doesn't warrant a new template — the existing one covers
            // the whole bundle and shouldn't be replaced with a single-entry file.
            // Saved at the bundle root (source.Parent) — same level as the environment YAML,
            // not buried inside the manifests folder.
            if (packageFilter is null && totalWritten > 0)
            {
                var bundleRoot  = source.Parent?.FullName ?? source.FullName;
                var templateFile = Path.Combine(bundleRoot, "deployment-template.yaml");
                var templateYaml = generator.RenderDeploymentTemplate(generatedManifests);
                File.WriteAllText(templateFile, templateYaml);
                AnsiConsole.MarkupLine($"Deployment template → [cyan]{Markup.Escape(templateFile)}[/]");
            }

            if (totalWritten > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Next steps:[/]");
                AnsiConsole.MarkupLine("[dim]  1. Review each .application.yaml — fill in any blank fields (display-name, description, paths).[/]");
                AnsiConsole.MarkupLine("[dim]  2. Copy entries from deployment-template.yaml into your <environment>.yaml deployments section.[/]");
                AnsiConsole.MarkupLine("[dim]     Fill in server names and any parameters marked '(no default)'.[/]");
                AnsiConsole.MarkupLine("[dim]  3. Set the service account in your environment manifest (domain-account.account).[/]");
                AnsiConsole.MarkupLine("[dim]  4. Add the manifests-path in your environment manifest to point to this directory.[/]");
                AnsiConsole.MarkupLine("[dim]  5. Run 'installer validate --env <your-env.yaml>' to check everything.[/]");
            }

            if (failed > 0) Environment.Exit(1);

        }, sourceOption, packageOption, outOption!, overwriteOption);

        return command;
    }
}
