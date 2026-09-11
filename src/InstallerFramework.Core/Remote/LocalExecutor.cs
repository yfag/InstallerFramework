using System.Management.Automation;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Remote;

/// <summary>
/// Executes operations on the local machine.
/// Used for deployments to localhost and for unit testing without a real server.
/// Implements the same interface as WinRmExecutor — pipeline steps are unaware of the difference.
/// </summary>
public sealed class LocalExecutor : IRemoteExecutor
{
    private readonly ILogger<LocalExecutor> _logger;

    public string ServerName => Environment.MachineName;

    public LocalExecutor(ILogger<LocalExecutor> logger) => _logger = logger;

    public async Task<RemoteExecutionResult> ExecuteScriptAsync(
        string script,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        using var ps = PowerShell.Create(RunspaceMode.NewRunspace);
        ps.AddScript(script);

        if (parameters != null)
            foreach (var (key, value) in parameters)
                ps.AddParameter(key, value);

        try
        {
            var results = await Task.Run(() => ps.Invoke(), cancellationToken);

            var output = string.Join(
                Environment.NewLine,
                results.Where(r => r is not null).Select(r => r.ToString()));

            var errors = string.Join(
                Environment.NewLine,
                ps.Streams.Error.Where(e => e is not null).Select(e => e.ToString()));

            return new RemoteExecutionResult(!ps.HadErrors, output, errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RemoteExecutionResult.Fail(ex.Message);
        }
    }

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task CopyFileToRemoteAsync(string localPath, string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        if (!localPath.Equals(remoteAbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(remoteAbsolutePath)!);
            File.Copy(localPath, remoteAbsolutePath, overwrite: true);
        }
        return Task.CompletedTask;
    }

    public Task CopyDirectoryToRemoteAsync(string localPath, string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        if (!localPath.Equals(remoteAbsolutePath, StringComparison.OrdinalIgnoreCase))
            CopyDirectoryRecursive(localPath, remoteAbsolutePath);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(remoteAbsolutePath));

    public Task<bool> DirectoryExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
        => Task.FromResult(Directory.Exists(remoteAbsolutePath));

    public Task<string> ReadFileAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(remoteAbsolutePath, cancellationToken);

    public async Task WriteFileAsync(string remoteAbsolutePath, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(remoteAbsolutePath)!);
        await File.WriteAllTextAsync(remoteAbsolutePath, content, cancellationToken);
    }

    public Task CreateDirectoryAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(remoteAbsolutePath);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string remoteAbsolutePath, bool recursive, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(remoteAbsolutePath))
            Directory.Delete(remoteAbsolutePath, recursive);
        return Task.CompletedTask;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
