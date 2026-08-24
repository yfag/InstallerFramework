using System.CommandLine;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

public static class InstallCommand
{
    public static Command Create(DeploymentService deploymentService)
    {
        var envOption = new Option<FileInfo>(
            aliases: ["--env", "-e"],
            description: "Path to the environment manifest YAML file.")
        { IsRequired = true };

        var appOption = new Option<string?>(
            aliases: ["--app", "-a"],
            description: "Install only this application (matches application name). Omit to install all.");

        var serverOption = new Option<string?>(
            aliases: ["--server", "-s"],
            description: "Install only on this server. Omit to install on all configured servers.");

        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Redeploy all applications even if they are already at the correct version.");

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run"],
            description: "Validate manifests only — do not connect to servers or make any changes.");

        var passwordOption = new Option<string?>(
            aliases: ["--password", "-p"],
            description: "Password for the domain account used by application identities. " +
                         "Only required when the environment uses regular domain accounts (use-gmsa: false). " +
                         "Can also be supplied via the INSTALLER_DOMAIN_PASSWORD environment variable. " +
                         "If neither is provided, the installer will prompt interactively.");

        var noRollbackOption = new Option<bool>(
            aliases: ["--no-rollback"],
            description: "Do not roll back completed steps if a deployment fails. " +
                         "The server is left in its current state so you can inspect configuration files, " +
                         "logs, and application state before retrying. " +
                         "Use when diagnosing startup or configuration errors.");

        var command = new Command("install", "Install or update applications on target servers.")
        {
            envOption, appOption, serverOption, forceOption, dryRunOption, passwordOption, noRollbackOption
        };

        command.SetHandler(async (envFile, app, server, force, dryRun, passwordArg, noRollback) =>
        {
            AnsiConsole.MarkupLine($"[bold]InstallerFramework[/] — install");
            AnsiConsole.MarkupLine($"Environment: [cyan]{Markup.Escape(envFile.FullName)}[/]");

            if (force)
                AnsiConsole.MarkupLine("[yellow]--force: redeploying all applications regardless of installed state[/]");

            if (noRollback)
                AnsiConsole.MarkupLine("[yellow]--no-rollback: server state will be preserved on failure[/]");

            if (dryRun)
            {
                AnsiConsole.MarkupLine("[yellow]DRY RUN — validating manifests only[/]");
                var errors = deploymentService.Validate(envFile.FullName);
                if (errors.Count == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓ Manifests are valid.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ {errors.Count} validation error(s):[/]");
                    foreach (var e in errors)
                        AnsiConsole.MarkupLine($"  [red]• {Markup.Escape(e)}[/]");
                    Environment.Exit(1);
                }
                return;
            }

            // --- Domain account passwords ---
            // Collect one password for each unique regular-domain-account found across the
            // deployments we are about to run.  gMSAs and built-in accounts are excluded.
            // The --password flag / INSTALLER_DOMAIN_PASSWORD env var supply the password for
            // the environment-level default account (the common case of a single shared account).
            // Any additional per-application override accounts always require an interactive prompt.
            var accountsNeeded = deploymentService.GetAccountsNeedingPasswords(
                envFile.FullName, app, server);

            var domainPasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (accountName, isEnvDefault) in accountsNeeded)
            {
                string? pw = null;

                if (isEnvDefault)
                {
                    // For the environment-default account: honour --password flag and env var first.
                    pw = passwordArg
                        ?? System.Environment.GetEnvironmentVariable("INSTALLER_DOMAIN_PASSWORD");
                }

                if (!string.IsNullOrEmpty(pw))
                {
                    AnsiConsole.MarkupLine(
                        $"[dim]Password for [bold]{Markup.Escape(accountName)}[/] supplied via argument/environment.[/]");
                }
                else
                {
                    if (isEnvDefault)
                        AnsiConsole.MarkupLine("[yellow]This environment uses regular domain accounts.[/]");
                    else
                        AnsiConsole.MarkupLine(
                            $"[yellow]Application uses a different account: [bold]{Markup.Escape(accountName)}[/][/]");

                    pw = AnsiConsole.Prompt(
                        new TextPrompt<string>($"Password for [bold]{Markup.Escape(accountName)}[/]:")
                            .Secret());
                }

                domainPasswords[accountName] = pw!;
            }

            try
            {
                var summary = await AnsiConsole.Status()
                    .StartAsync("Deploying...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await deploymentService.InstallAsync(
                            envFile.FullName, app, server, force, domainPasswords, noRollback);
                    });

                PrintSummary(summary);
                if (!summary.AllSucceeded) Environment.Exit(1);
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
            }
        }, envOption, appOption, serverOption, forceOption, dryRunOption, passwordOption, noRollbackOption);

        return command;
    }

    internal static void PrintSummary(Core.Models.EnvironmentDeploymentSummary summary, bool isUninstall = false)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Application")
            .AddColumn("Server")
            .AddColumn("Result")
            .AddColumn("Version")
            .AddColumn("Duration");

        foreach (var r in summary.Results)
        {
            string resultMark, version, duration;

            if (r.Skipped)
            {
                resultMark = isUninstall ? "[dim]— not installed[/]" : "[dim]— up to date[/]";
                version    = isUninstall ? "[dim]—[/]" : $"v{Markup.Escape(r.InstalledVersion ?? "-")}";
                duration   = "[dim]—[/]";
            }
            else if (r.Success)
            {
                var arrow  = r.PreviousVersion is not null
                    ? $"v{Markup.Escape(r.PreviousVersion)} → v{Markup.Escape(r.InstalledVersion ?? "-")}"
                    : $"v{Markup.Escape(r.InstalledVersion ?? "-")} (new)";
                resultMark = isUninstall ? "[green]✓ removed[/]" : "[green]✓ deployed[/]";
                version    = arrow;
                duration   = $"{r.Duration.TotalSeconds:F1}s";
            }
            else
            {
                resultMark = "[red]✗ failed[/]";
                version    = Markup.Escape(r.FailedStep ?? "-");
                duration   = $"{r.Duration.TotalSeconds:F1}s";
            }

            table.AddRow(
                Markup.Escape(r.ApplicationName),
                Markup.Escape(r.TargetServer),
                resultMark,
                version,
                duration);
        }

        AnsiConsole.Write(table);

        // Summary line: e.g. "3 deployed, 54 up to date, 0 failed — 12.4s"
        var parts = new List<string>();
        if (summary.SuccessCount > 0)
            parts.Add(isUninstall ? $"[green]{summary.SuccessCount} removed[/]" : $"[green]{summary.SuccessCount} deployed[/]");
        if (summary.SkippedCount > 0)
            parts.Add(isUninstall
                ? $"[dim]{summary.SkippedCount} not installed[/]"
                : $"[dim]{summary.SkippedCount} up to date[/]");
        if (summary.FailureCount > 0)
            parts.Add($"[red]{summary.FailureCount} failed[/]");

        AnsiConsole.MarkupLine(
            $"\n{string.Join(", ", parts)} — " +
            $"[dim]{summary.TotalDuration.TotalSeconds:F1}s total[/]");

        foreach (var failure in summary.Results.Where(r => !r.Success))
        {
            AnsiConsole.MarkupLine(
                $"[red]FAILURE[/] {Markup.Escape(failure.ApplicationName)}@{Markup.Escape(failure.TargetServer)}: " +
                $"step '{Markup.Escape(failure.FailedStep ?? "?")}' — {Markup.Escape(failure.ErrorMessage ?? "unknown")}");
            if (failure.RolledBack)
                AnsiConsole.MarkupLine("  [yellow]→ Rolled back to previous version.[/]");
            else if (failure.RollbackFailed)
                AnsiConsole.MarkupLine("  [red bold]→ ROLLBACK FAILED — manual intervention required.[/]");
            else if (failure.RollbackSkipped)
                AnsiConsole.MarkupLine("  [dim]→ Rollback skipped (--no-rollback) — server state preserved for inspection.[/]");
        }
    }
}
