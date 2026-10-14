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

            LogRetention.Prune(envManifest.Environment.LogRetentionFiles);

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

            var counts = new Dictionary<AppState, int>();

            foreach (var deployment in deployments)
            {
                if (!appManifests.TryGetValue(deployment.Application, out var appManifest)) continue;

                var servers = deployment.Servers
                    .Where(s => server is null || s.Equals(server, StringComparison.OrdinalIgnoreCase));

                foreach (var targetServer in servers)
                {
                    var (installedVersion, state, status) = await GetServerStatusAsync(
                        appManifest, deployment, targetServer, envManifest.Environment, loggerFactory);

                    counts[state] = counts.GetValueOrDefault(state) + 1;

                    // Running = green, installed but stopped = red, not installed = yellow, error = red.
                    var color = state switch
                    {
                        AppState.Running => "green",
                        AppState.Stopped => "red",
                        AppState.Error   => "red",
                        _                => "yellow"
                    };
                    var statusMark = $"[{color}]{Markup.Escape(status)}[/]";

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

            // Summary — counted per application × server, so an app deployed to two servers counts twice.
            var running   = counts.GetValueOrDefault(AppState.Running);
            var stopped   = counts.GetValueOrDefault(AppState.Stopped);
            var notInst   = counts.GetValueOrDefault(AppState.NotInstalled);
            var errors    = counts.GetValueOrDefault(AppState.Error);

            var summary = $"Installed: [bold]{running + stopped}[/]   " +
                          $"Running: [green]{running}[/]   " +
                          $"Stopped: {(stopped > 0 ? $"[red]{stopped}[/]" : stopped.ToString())}";
            if (notInst > 0) summary += $"   Not installed: [yellow]{notInst}[/]";
            if (errors > 0)  summary += $"   Errors: [red]{errors}[/]";
            AnsiConsole.MarkupLine(summary);
        }, envOption, appOption, serverOption);

        return command;
    }

    private enum AppState { Running, Stopped, NotInstalled, Error }

    /// <summary>
    /// Classifies the status text returned by an adapter's <c>GetStatusAsync</c>.
    /// Installed = Running or Stopped. "Stopped" means installed but not running, including
    /// any non-running service state (Paused, StartPending, ...) and a partially registered IIS app.
    /// </summary>
    private static AppState Classify(ApplicationType type, string status)
    {
        if (status.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return AppState.Error;
        if (status.StartsWith("Not installed", StringComparison.OrdinalIgnoreCase)) return AppState.NotInstalled;

        if (type == ApplicationType.IisApplication)
        {
            // Format: "Pool: <state|Not installed> | App: <Registered|Not registered> | Identity: ..."
            var poolStarted   = status.Contains("Pool: Started", StringComparison.OrdinalIgnoreCase);
            var poolMissing   = status.Contains("Pool: Not installed", StringComparison.OrdinalIgnoreCase);
            var appRegistered = status.Contains("App: Registered", StringComparison.OrdinalIgnoreCase);

            if (poolMissing && !appRegistered) return AppState.NotInstalled;
            return poolStarted && appRegistered ? AppState.Running : AppState.Stopped;
        }

        // Windows Service format: "<Status> (Identity: ...)"
        return status.StartsWith("Running", StringComparison.OrdinalIgnoreCase)
            ? AppState.Running
            : AppState.Stopped;
    }

    private static async Task<(string? InstalledVersion, AppState State, string Status)> GetServerStatusAsync(
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
            var installDir = context.EffectiveInstallDirectory;
            if (string.IsNullOrWhiteSpace(installDir)) installDir = null;

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
            return (installedVersion, Classify(appManifest.Application.Type, status), status);
        }
        catch (Exception ex)
        {
            return (null, AppState.Error, $"Error: {ex.Message}");
        }
    }
}
