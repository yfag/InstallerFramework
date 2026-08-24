using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Adapters;

public sealed class ApplicationAdapterFactory : IApplicationAdapterFactory
{
    private readonly ILogger<WindowsServiceAdapter> _serviceLogger;
    private readonly ILogger<IisAdapter> _iisLogger;

    public ApplicationAdapterFactory(
        ILogger<WindowsServiceAdapter> serviceLogger,
        ILogger<IisAdapter> iisLogger)
    {
        _serviceLogger = serviceLogger;
        _iisLogger = iisLogger;
    }

    public IApplicationAdapter Create(DeploymentContext context) =>
        context.ApplicationManifest.Application.Type switch
        {
            ApplicationType.WindowsService => new WindowsServiceAdapter(_serviceLogger),
            ApplicationType.IisApplication => new IisAdapter(_iisLogger),
            _ => throw new NotSupportedException(
                $"Application type '{context.ApplicationManifest.Application.Type}' is not supported.")
        };
}
