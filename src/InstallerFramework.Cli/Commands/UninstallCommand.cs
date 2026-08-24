using System.CommandLine;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

public static class UninstallCommand
{
    public static Command Create(DeploymentService deploymentService)
    {
        var envOption = new Option<FileInfo>(
            aliases: ["--env", "-e"],
            description: "Path to the environment manifest YAML file.")
        { IsRequired = true };

        var appOption = new Option<string?>(
            aliases: ["--app", "-a"],
            description: "Application to uninstall. Mutually exclusive with --all.");

        var allOption = new Option<bool>(
            aliases: ["--all"],
            description: "Uninstall every application defined in the environment manifest. " +
                         "Mutually exclusive with --app.");

        var serverOption = new Option<string?>(
            aliases: ["--server", "-s"],
            description: "Uninstall from this server only. Omit to uninstall from all configured servers.");

        var command = new Command("uninstall", "Uninstall one or all applications from target servers.")
        {
            envOption, appOption, allOption, serverOption
        };

        command.SetHandler(async (envFile, app, all, server) =>
        {
            // --- Validate mutually exclusive options ---
            if (!all && string.IsNullOrWhiteSpace(app))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Specify either [bold]--app <name>[/] or [bold]--all[/].");
                Environment.Exit(1);
                return;
            }

            if (all && !string.IsNullOrWhiteSpace(app))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] [bold]--app[/] and [bold]--all[/] are mutually exclusive.");
                Environment.Exit(1);
                return;
            }

            AnsiConsole.MarkupLine($"[bold]InstallerFramework[/] — uninstall");

            // Describe what is about to happen
            var scope = all
                ? "[red bold]ALL applications[/]"
                : $"[bold]{Markup.Escape(app!)}[/]";
            var target = server is not null
                ? $"[cyan]{Markup.Escape(server)}[/]"
                : "all configured servers";

            AnsiConsole.MarkupLine($"[yellow]Uninstalling[/] {scope} from {target}");

            if (all)
                AnsiConsole.MarkupLine("[red]WARNING:[/] This will remove every application in the environment manifest.");

            // Confirmation prompt
            if (!AnsiConsole.Confirm("Are you sure you want to uninstall?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                return;
            }

            try
            {
                // Pass null for applicationName when --all is specified so the service
                // iterates over every deployment in the manifest.
                var summary = await AnsiConsole.Status()
                    .StartAsync("Uninstalling...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await deploymentService.UninstallAsync(
                            envFile.FullName,
                            applicationName: all ? null : app,
                            serverFilter: server);
                    });

                InstallCommand.PrintSummary(summary, isUninstall: true);
                if (!summary.AllSucceeded) Environment.Exit(1);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
            }
        }, envOption, appOption, allOption, serverOption);

        return command;
    }
}
