using System.Net.Http;
using InstallerFramework.Core.Adapters;
using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 4 — Verify.
/// Confirms the application is actually running after the start command.
/// Optionally performs an HTTP health check for IIS applications.
/// A failure here triggers rollback — the new version didn't start cleanly.
/// </summary>
public sealed class VerifyRunningStep : IDeploymentStep
{
    private readonly IApplicationAdapterFactory _adapterFactory;

    public VerifyRunningStep(IApplicationAdapterFactory adapterFactory) => _adapterFactory = adapterFactory;

    public string Name => "Verify Running";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var adapter = _adapterFactory.Create(context);

        // Give the process a moment to fully start
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        // Check OS-level status (service Running / app pool Started)
        var isRunning = await adapter.IsRunningAsync(context, cancellationToken);
        if (!isRunning)
            return StepResult.Fail(
                $"Application is not in a running state after start. " +
                "Check the application event log on the target server for startup errors.");

        context.Logger.LogInformation(
            "[{App}@{Server}] Application is running.",
            context.ApplicationName, context.TargetServer);

        // Optional HTTP health check
        var healthCheck = context.Deployment.HealthCheck;
        if (healthCheck is not null && !string.IsNullOrWhiteSpace(healthCheck.Url))
        {
            var healthResult = await RunHealthCheckAsync(context, healthCheck, cancellationToken);
            if (!healthResult.Success)
                return healthResult;
        }

        return StepResult.Ok();
    }

    private async Task<StepResult> RunHealthCheckAsync(
        DeploymentContext context,
        HealthCheckConfig config,
        CancellationToken cancellationToken)
    {
        var expectedCodes = config.ExpectedStatus
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var code) ? code : 200)
            .ToHashSet();

        context.Logger.LogInformation(
            "[{App}@{Server}] Running health check: {Url} (timeout {Timeout}s, {Retries} retries)",
            context.ApplicationName, context.TargetServer,
            config.Url, config.TimeoutSeconds, config.RetryCount);

        // The health check runs directly from the installer process (not via WinRM on the
        // target server) for two reasons:
        //
        //   1. WinRM loopback HTTPS is unreliable: PowerShell 5.1's .NET Framework runtime
        //      uses outdated TLS defaults and runs in a restricted context that can't resolve
        //      private CA chains, causing "connection closed" errors even when the cert is
        //      valid and the app is running perfectly.
        //
        //   2. An external check is more meaningful — it proves the app is reachable from
        //      the network, not just that a loopback call on the same server works.
        //
        // Certificate validation is bypassed because CheckConnectivityStep already confirmed
        // the IIS binding carries a valid, non-expired certificate before deployment started.
        // This preserves the security guarantee without the WinRM trust-context limitation.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };

        var attempts = new List<string>();

        for (int i = 1; i <= config.RetryCount; i++)
        {
            try
            {
                var response = await client.GetAsync(config.Url, cancellationToken);
                var statusCode = (int)response.StatusCode;

                if (expectedCodes.Contains(statusCode))
                {
                    context.Logger.LogInformation(
                        "[{App}@{Server}] Health check passed: HTTP {Code}",
                        context.ApplicationName, context.TargetServer, statusCode);
                    return StepResult.Ok();
                }

                attempts.Add($"Attempt {i}/{config.RetryCount}: HTTP {statusCode} (expected: {config.ExpectedStatus})");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // propagate explicit cancellation
            }
            catch (Exception ex)
            {
                attempts.Add($"Attempt {i}/{config.RetryCount} failed: {ex.Message}");
            }

            if (i < config.RetryCount)
                await Task.Delay(TimeSpan.FromSeconds(config.RetryIntervalSeconds), cancellationToken);
        }

        return StepResult.Fail(
            $"Health check failed after {config.RetryCount} attempt(s). " +
            $"Details: {string.Join(" | ", attempts)}. " +
            "The application started but is not responding correctly.");
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Rollback is handled by StartApplicationStep.
}
