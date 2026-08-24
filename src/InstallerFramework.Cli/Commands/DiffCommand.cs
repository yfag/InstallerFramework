using System.CommandLine;
using InstallerFramework.Core.Configuration;
using InstallerFramework.Core.Models;
using InstallerFramework.Core.Packages;
using InstallerFramework.Core.Remote;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

/// <summary>
/// Shows what 'installer install' WOULD do without making any changes.
/// For each application/server pair, reports: Install (new), Update (version change), Up to date, or Error.
/// </summary>
public static class DiffCommand
{
    public static Command Create(DeploymentService deploymentService, ILoggerFactory loggerFactory)
    {
        var envOption = new Option<FileInfo>(
            aliases: ["--env", "-e"],
            description: "Path to the environment manifest YAML file.")
        { IsRequired = true };

        var appOption = new Option<string?>(
            aliases: ["--app", "-a"],
            description: "Show diff for this application only.");

        var command = new Command(
            "diff",
            "Show what changes 'install' would make — reads server state, makes no changes.")
        {
            envOption, appOption
        };

        command.SetHandler(async (envFile, app) =>
        {
            var loader = new ManifestLoader();
            var resolver = new NuGetPackageResolver();

            EnvironmentManifest envManifest;
            Dictionary<string, ApplicationManifest> appManifests;

            try
            {
                envManifest = loader.LoadEnvironmentManifest(envFile.FullName);
                appManifests = loader.LoadApplicationManifests(envManifest, envFile.FullName);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
                return;
            }

            // Validate manifests first
            var validator = new ManifestValidator();
            var errors = validator.Validate(envManifest, appManifests);
            if (errors.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]Manifest validation errors (fix before deploying):[/]");
                foreach (var e in errors) AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]Diff — {Markup.Escape(envManifest.Environment.Name)}[/]")
                .AddColumn("Application")
                .AddColumn("Server")
                .AddColumn("Installed")
                .AddColumn("Target")
                .AddColumn("Action")
                .AddColumn("Package Found?");

            var deployments = envManifest.Environment.Deployments
                .Where(d => app is null || d.Application.Equals(app, StringComparison.OrdinalIgnoreCase));

            foreach (var deployment in deployments)
            {
                if (!appManifests.TryGetValue(deployment.Application, out var appManifest)) continue;

                // Check if package exists (no server needed)
                var packageSource = deployment.PackageSource
                    ?? envManifest.Environment.DefaultPackageSource
                    ?? appManifest.Application.Package.Source;

                var targetVersion = DeploymentService.ResolveEffectiveVersion(appManifest, deployment);
                var packagePath = await resolver.ResolveAsync(
                    packageSource, appManifest.Application.Package.Id, targetVersion);
                var packageFound = packagePath is not null;

                foreach (var server in deployment.Servers)
                {
                    var (installedVersion, action) = await GetDiffAsync(appManifest, deployment, server, loggerFactory);

                    var actionMark = action switch
                    {
                        "Install" => "[green]Install (new)[/]",
                        "Update"  => "[cyan]Update[/]",
                        "Current" => "[dim]Up to date[/]",
                        _         => $"[yellow]{Markup.Escape(action)}[/]"
                    };

                    table.AddRow(
                        Markup.Escape(deployment.Application),
                        Markup.Escape(server),
                        installedVersion is not null ? $"v{Markup.Escape(installedVersion)}" : "[dim]not installed[/]",
                        $"v{Markup.Escape(targetVersion)}",
                        actionMark,
                        packageFound ? "[green]✓[/]" : "[red]✗ NOT FOUND[/]");
                }
            }

            AnsiConsole.Write(table);
        }, envOption, appOption);

        return command;
    }

    private static async Task<(string? InstalledVersion, string Action)> GetDiffAsync(
        ApplicationManifest appManifest,
        ApplicationDeployment deployment,
        string server,
        ILoggerFactory loggerFactory)
    {
        try
        {
            await using IRemoteExecutor remote = server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new LocalExecutor(loggerFactory.CreateLogger<LocalExecutor>())
                : await WinRmExecutor.ConnectAsync(server, loggerFactory.CreateLogger<WinRmExecutor>());

            var installDir = appManifest.Application.Type switch
            {
                ApplicationType.WindowsService => appManifest.Application.Service?.InstallDirectory,
                ApplicationType.IisApplication => appManifest.Application.Iis?.PhysicalPath,
                _ => null
            };

            if (installDir is null) return (null, "Unknown");

            var versionFile = Path.Combine(installDir, ".installer-version");
            if (!await remote.FileExistsAsync(versionFile))
                return (null, "Install");

            var installedVersion = (await remote.ReadFileAsync(versionFile)).Trim();
            var targetVer = DeploymentService.ResolveEffectiveVersion(appManifest, deployment);
            if (installedVersion.Equals(targetVer, StringComparison.OrdinalIgnoreCase))
                return (installedVersion, "Current");

            return (installedVersion, "Update");
        }
        catch (Exception ex)
        {
            return (null, $"Error: {ex.Message}");
        }
    }
}
