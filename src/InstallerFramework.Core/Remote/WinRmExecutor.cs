using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Remote;

/// <summary>
/// Executes PowerShell commands on a remote Windows server via WinRM.
/// Uses Kerberos authentication — the operator's AD token is passed transparently.
/// No credentials are stored or prompted.
///
/// File operations use admin shares (\\SERVER\C$\...) for efficiency — avoids
/// streaming large .nupkg files through the PowerShell runspace.
/// </summary>
public sealed class WinRmExecutor : IRemoteExecutor
{
    private readonly Runspace _runspace;
    private readonly ILogger<WinRmExecutor> _logger;
    private bool _disposed;

    public string ServerName { get; }

    private WinRmExecutor(string serverName, Runspace runspace, ILogger<WinRmExecutor> logger)
    {
        ServerName = serverName;
        _runspace = runspace;
        _logger = logger;
    }

    /// <summary>
    /// Opens a WinRM connection to the target server using Kerberos (domain credential passthrough).
    /// Throws if the server is unreachable or WinRM is not configured.
    /// </summary>
    public static async Task<WinRmExecutor> ConnectAsync(
        string serverName,
        ILogger<WinRmExecutor> logger,
        int port = 5985,
        bool useHttps = false,
        CancellationToken cancellationToken = default)
    {
        var scheme = useHttps ? "https" : "http";
        var connectionInfo = new WSManConnectionInfo(new Uri($"{scheme}://{serverName}:{port}/wsman"))
        {
            AuthenticationMechanism = AuthenticationMechanism.Kerberos,
            OperationTimeout    = 120_000, // 2 min for long-running operations
            OpenTimeout         = 30_000
        };

        logger.LogDebug("Opening WinRM connection to {Server}:{Port}", serverName, port);
        var runspace = RunspaceFactory.CreateRunspace(connectionInfo);

        await Task.Run(() => runspace.Open(), cancellationToken);

        logger.LogDebug("WinRM connection established to {Server}", serverName);
        return new WinRmExecutor(serverName, runspace, logger);
    }

    public async Task<RemoteExecutionResult> ExecuteScriptAsync(
        string script,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
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

            if (ps.HadErrors)
                _logger.LogDebug("[{Server}] Script errors: {Errors}", ServerName, errors);

            return new RemoteExecutionResult(!ps.HadErrors, output, errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[{Server}] Script execution threw an exception", ServerName);
            return RemoteExecutionResult.Fail(ex.Message);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScriptAsync("$env:COMPUTERNAME", cancellationToken: cancellationToken);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output);
    }

    public Task CopyFileToRemoteAsync(
        string localPath,
        string remoteAbsolutePath,
        CancellationToken cancellationToken = default)
    {
        var uncPath = ToAdminSharePath(ServerName, remoteAbsolutePath);
        _logger.LogDebug("Copying {Local} → {Unc}", localPath, uncPath);

        return Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(uncPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.Copy(localPath, uncPath, overwrite: true);
        }, cancellationToken);
    }

    public Task CopyDirectoryToRemoteAsync(
        string localPath,
        string remoteAbsolutePath,
        CancellationToken cancellationToken = default)
    {
        var uncPath = ToAdminSharePath(ServerName, remoteAbsolutePath);
        _logger.LogDebug("Copying directory {Local} → {Unc}", localPath, uncPath);

        return Task.Run(() => CopyDirectoryRecursive(localPath, uncPath), cancellationToken);
    }

    public async Task<bool> FileExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScriptAsync(
            $"Test-Path -Path '{EscapePs(remoteAbsolutePath)}' -PathType Leaf",
            cancellationToken: cancellationToken);
        return result.Success && result.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DirectoryExistsAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScriptAsync(
            $"Test-Path -Path '{EscapePs(remoteAbsolutePath)}' -PathType Container",
            cancellationToken: cancellationToken);
        return result.Success && result.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ReadFileAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        var uncPath = ToAdminSharePath(ServerName, remoteAbsolutePath);
        return await File.ReadAllTextAsync(uncPath, cancellationToken);
    }

    public async Task WriteFileAsync(string remoteAbsolutePath, string content, CancellationToken cancellationToken = default)
    {
        var uncPath = ToAdminSharePath(ServerName, remoteAbsolutePath);
        var dir = Path.GetDirectoryName(uncPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(uncPath, content, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string remoteAbsolutePath, CancellationToken cancellationToken = default)
    {
        await ExecuteScriptAsync(
            $"New-Item -ItemType Directory -Path '{EscapePs(remoteAbsolutePath)}' -Force | Out-Null",
            cancellationToken: cancellationToken);
    }

    public async Task DeleteDirectoryAsync(string remoteAbsolutePath, bool recursive, CancellationToken cancellationToken = default)
    {
        var recurseFlag = recursive ? "-Recurse" : string.Empty;
        await ExecuteScriptAsync(
            $"Remove-Item -Path '{EscapePs(remoteAbsolutePath)}' {recurseFlag} -Force -ErrorAction SilentlyContinue",
            cancellationToken: cancellationToken);
    }

    // --- Helpers ---

    private static string ToAdminSharePath(string server, string localPath)
    {
        // C:\Some\Path  →  \\SERVER\C$\Some\Path
        if (localPath.Length < 3 || localPath[1] != ':')
            throw new ArgumentException($"Cannot convert to admin share path: '{localPath}'. Must be an absolute path like C:\\...");

        var drive = localPath[0];
        var rest = localPath.Length > 3 ? localPath[3..] : string.Empty; // skip "C:\"
        return $@"\\{server}\{drive}$\{rest}";
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>Escape single quotes for PowerShell string literals.</summary>
    private static string EscapePs(string path) => path.Replace("'", "''");

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { _runspace.Close(); } catch { /* best effort */ }
            _runspace.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
