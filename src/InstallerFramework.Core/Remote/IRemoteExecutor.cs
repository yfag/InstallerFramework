namespace InstallerFramework.Core.Remote;

/// <summary>
/// Abstracts remote (WinRM) vs local execution.
/// All deployment steps use this interface — never call WinRM directly.
/// This makes unit testing straightforward by substituting a local or mock executor.
/// </summary>
public interface IRemoteExecutor : IAsyncDisposable
{
    string ServerName { get; }

    /// <summary>Execute a PowerShell script block on the target server.</summary>
    Task<RemoteExecutionResult> ExecuteScriptAsync(
        string script,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Test connectivity and confirm we can execute commands on the target.</summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Copy a file from the local machine to the target server via admin share (\\SERVER\C$\...).
    /// Requires local admin rights on the target, which is a pre-requisite of this framework.
    /// </summary>
    Task CopyFileToRemoteAsync(
        string localPath,
        string remoteAbsolutePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copy a directory recursively from the local machine to the target server via admin share.
    /// </summary>
    Task CopyDirectoryToRemoteAsync(
        string localPath,
        string remoteAbsolutePath,
        CancellationToken cancellationToken = default);

    Task<bool> FileExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default);

    Task<bool> DirectoryExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default);

    Task<string> ReadFileAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string remoteAbsolutePath, string content, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(string remoteAbsolutePath, bool recursive, CancellationToken cancellationToken = default);
}

public sealed record RemoteExecutionResult(
    bool Success,
    string Output,
    string Errors,
    int ExitCode = 0)
{
    public static RemoteExecutionResult Ok(string output = "") => new(true, output, string.Empty, 0);
    public static RemoteExecutionResult Fail(string errors, int exitCode = 1) => new(false, string.Empty, errors, exitCode);

    public override string ToString() =>
        Success ? $"OK: {Output}" : $"FAILED (exit {ExitCode}): {Errors}";
}
