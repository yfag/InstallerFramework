using System.CommandLine;
using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Configuration;
using InstallerFramework.Core.Models;
using InstallerFramework.Core.Remote;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

public static class StatusCommand
{
    public static Command Create(DeploymentService deploymentService, ILoggerFactory loggerFactory)
    {
        var envOption = new Option<FileInfo>(
            aliases: ["--env", "-e"],
            description: "Path to the environment manifest YAML file.")
        { IsRequired = true };

        var appOption = new Option<string?>(
            aliases: ["--app", "-a"],
            description: "Check status for this application only.");

        var serverOption = new Option<string?>(
            aliases: ["--server", "-s"],
            description: "Check status on this server only.");

        var command = new Command(
            "status",
            "Show the running status of applications on their configured servers.")
        {
            envOption, appOption, serverOption
        };

        command.SetHandler(async (envFile, app, server) =>
        {
            var loader = new ManifestLoader();
            EnvironmentManifest envManifest;
            Dictionary<string, ApplicationManifest> appManifests;

            try
            {
                envManifest = loader.LoadEnvironmentManifest(envFile.FullName);
                appManifests = loader.LoadApplicationManifests(envManifest, envFile.FullName);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading manifests:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]Status — {Markup.Escape(envManifest.Environment.Name)}[/]")
                .AddColumn("Application")
                .AddColumn("Server")
                .AddColumn("Target Version")
                .AddColumn("Installed Version")
                .AddColumn("Status");

            var deployments = envManifest.Environment.Deployments
                .Where(d => app is null || d.Application.Equals(app, StringComparison.OrdinalIgnoreCase));

            foreach (var deployment in deployments)
            {
                if (!appManifests.TryGetValue(deployment.Application, out var appManifest)) continue;

                var servers = deployment.Servers
                    .Where(s => server is null || s.Equals(server, StringComparison.OrdinalIgnoreCase));

                foreach (var targetServer in servers)
                {
                    var (installedVersion, status) = await GetServerStatusAsync(
                        appManifest, deployment, targetServer, envManifest.Environment, loggerFactory);

                    var statusMark = status.StartsWith("Running", StringComparison.OrdinalIgnoreCase) ||
                                     status.StartsWith("Started", StringComparison.OrdinalIgnoreCase)
                        ? $"[green]{Markup.Escape(status)}[/]"
                        : $"[yellow]{Markup.Escape(status)}[/]";

                    var targetVersion = DeploymentService.ResolveEffectiveVersion(appManifest, deployment);
                    table.AddRow(
                        Markup.Escape(deployment.Application),
                        Markup.Escape(targetServer),
                        $"v{Markup.Escape(targetVersion)}",
                        installedVersion is not null ? $"v{Markup.Escape(installedVersion)}" : "[dim]unknown[/]",
                        statusMark);
                }
            }

            AnsiConsole.Write(table);
        }, envOption, appOption, serverOption);

        return command;
    }

    private static async Task<(string? InstalledVersion, string Status)> GetServerStatusAsync(
        ApplicationManifest appManifest,
        ApplicationDeployment deployment,
        string server,
        EnvironmentConfig environment,
        ILoggerFactory loggerFactory)
    {
        try
        {
            await using IRemoteExecutor remote = server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new LocalExecutor(loggerFactory.CreateLogger<LocalExecutor>())
                : await WinRmExecutor.ConnectAsync(server, loggerFactory.CreateLogger<WinRmExecutor>());

            var context = new DeploymentContext
            {
                ApplicationManifest = appManifest,
                Deployment          = deployment,
                TargetServer        = server,
                Remote              = remote,
                Logger              = loggerFactory.CreateLogger(appManifest.Application.Name),
                EnvironmentName     = "status-check",
                BackupRoot           = string.Empty,
                BackupRetentionDays  = 0,
                EffectiveParameters  = deployment.Parameters,  // status checks don't apply config
                DefaultPackageSource = null,
                EffectiveAccount     = DeploymentService.ResolveEffectiveAccount(appManifest, environment)
            };

            // Read installed version marker
            var installDir = appManifest.Application.Type switch
            {
                ApplicationType.WindowsService => appManifest.Application.Service?.InstallDirectory,
                ApplicationType.IisApplication => appManifest.Application.Iis?.PhysicalPath,
                _ => null
            };

            string? installedVersion = null;
            if (installDir is not null)
            {
                var versionFile = Path.Combine(installDir, ".installer-version");
                if (await remote.FileExistsAsync(versionFile))
                    installedVersion = (await remote.ReadFileAsync(versionFile)).Trim();
            }

            // Get running status from the adapter
            IApplicationAdapter adapter = appManifest.Application.Type switch
            {
                ApplicationType.WindowsService => new WindowsServiceAdapter(loggerFactory.CreateLogger<WindowsServiceAdapter>()),
                ApplicationType.IisApplication => new IisAdapter(loggerFactory.CreateLogger<IisAdapter>()),
                _ => throw new NotSupportedException()
            };

            var status = await adapter.GetStatusAsync(context);
            return (installedVersion, status);
        }
        catch (Exception ex)
        {
            return (null, $"[red]Error: {ex.Message}[/]");
        }
    }
}
