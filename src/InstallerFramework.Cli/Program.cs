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

// --- Logging setup ---
// Console: Information and above
// File: Debug and above, rolling daily, in %LOCALAPPDATA%\InstallerFramework\logs\
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "InstallerFramework", "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDir, "installer-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
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
