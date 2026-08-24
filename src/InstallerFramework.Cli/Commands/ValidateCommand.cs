using System.CommandLine;
using Spectre.Console;

namespace InstallerFramework.Cli.Commands;

public static class ValidateCommand
{
    public static Command Create(DeploymentService deploymentService)
    {
        var envOption = new Option<FileInfo>(
            aliases: ["--env", "-e"],
            description: "Path to the environment manifest YAML file.")
        { IsRequired = true };

        var command = new Command(
            "validate",
            "Validate environment and application manifests without connecting to any server.")
        {
            envOption
        };

        command.SetHandler((envFile) =>
        {
            AnsiConsole.MarkupLine($"[bold]Validating:[/] {Markup.Escape(envFile.FullName)}");

            try
            {
                var errors = deploymentService.Validate(envFile.FullName);

                if (errors.Count == 0)
                {
                    AnsiConsole.MarkupLine("[green]✓ All manifests are valid.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ {errors.Count} validation error(s):[/]");
                    foreach (var error in errors)
                        AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(error)}");
                    Environment.Exit(1);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading manifests:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
            }
        }, envOption);

        return command;
    }
}
