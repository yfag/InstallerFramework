using System.CommandLine;
using InstallerFramework.Cli;
using InstallerFramework.Cli.Commands;
using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Configuration;
using InstallerFramework.Core.Packages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// --- Detect --verbose before Serilog is configured ---
// System.CommandLine hasn't parsed yet at this point, so we pre-scan args directly.
var verbose = args.Contains("--verbose") || args.Contains("-v");

// --- Logging setup ---
// Console: Warning and above (quiet default) — or Information and above with --verbose.
//          Spectre.Console output (spinner, summary table) is unaffected by this setting.
// File:    Debug and above, one timestamped file per run, in a 'log' subfolder next to the exe.
var logDir  = Path.Combine(AppContext.BaseDirectory, "log");
Directory.CreateDirectory(logDir);
var logFile = Path.Combine(logDir, $"installer_{DateTime.Now:yyyyMMdd_HHmmss}.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        restrictedToMinimumLevel: verbose ? LogEventLevel.Information : LogEventLevel.Warning,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: logFile,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// --- DI container ---
var services = new ServiceCollection();

services.AddLogging(b => b
    .ClearProviders()
    .AddSerilog(Log.Logger));

services.AddSingleton<ManifestLoader>();
services.AddSingleton<ManifestValidator>();
services.AddSingleton<ConfigurationMerger>();
services.AddSingleton<NuGetPackageResolver>();
services.AddSingleton<IApplicationAdapterFactory, ApplicationAdapterFactory>();
services.AddSingleton<DeploymentService>();

var provider = services.BuildServiceProvider();
var deploymentService = provider.GetRequiredService<DeploymentService>();

// --- CLI definition ---
var rootCommand = new RootCommand("InstallerFramework — deploy .nupkg applications to Windows servers");

// Global option — consumed above by the pre-scan; registered here so System.CommandLine
// accepts it on any sub-command without treating it as an unknown argument.
rootCommand.AddGlobalOption(new Option<bool>(
    aliases: ["--verbose", "-v"],
    description: "Show detailed step-by-step log output on the console. " +
                 "Full logs are always written to the log/ folder next to the exe."));

rootCommand.AddCommand(PrepareCommand.Create());
rootCommand.AddCommand(InstallCommand.Create(deploymentService));
rootCommand.AddCommand(UninstallCommand.Create(deploymentService));
rootCommand.AddCommand(ValidateCommand.Create(deploymentService));
rootCommand.AddCommand(StatusCommand.Create(deploymentService, provider.GetRequiredService<ILoggerFactory>()));
rootCommand.AddCommand(DiffCommand.Create(deploymentService, provider.GetRequiredService<ILoggerFactory>()));

try
{
    return await rootCommand.InvokeAsync(args);
}
finally
{
    await Log.CloseAndFlushAsync();
}
