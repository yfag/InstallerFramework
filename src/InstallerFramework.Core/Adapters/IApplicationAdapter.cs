using InstallerFramework.Core.Models;

namespace InstallerFramework.Core.Adapters;

/// <summary>
/// Abstracts OS-level application lifecycle operations for a specific application type.
/// Two concrete implementations: WindowsServiceAdapter and IisAdapter.
/// All operations execute via the IRemoteExecutor on the target server — never locally.
/// </summary>
public interface IApplicationAdapter
{
    Task<bool> IsRunningAsync(DeploymentContext context, CancellationToken cancellationToken = default);
    Task StopAsync(DeploymentContext context, CancellationToken cancellationToken = default);
    Task StartAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>Create or update the service / IIS application registration.</summary>
    Task RegisterAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>Remove the service / IIS application registration entirely.</summary>
    Task UnregisterAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>Returns a human-readable status string (e.g. "Running", "Stopped", "Not installed").</summary>
    Task<string> GetStatusAsync(DeploymentContext context, CancellationToken cancellationToken = default);
}

/// <summary>Creates the correct adapter based on application type.</summary>
public interface IApplicationAdapterFactory
{
    IApplicationAdapter Create(DeploymentContext context);
}
