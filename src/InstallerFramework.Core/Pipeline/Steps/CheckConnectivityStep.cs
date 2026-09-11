using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 1 — Pre-flight.
/// Verifies WinRM connectivity and confirms the operator has admin access.
/// Also captures the currently installed version for rollback logging.
/// No changes made.
/// </summary>
public sealed class CheckConnectivityStep : IDeploymentStep
{
    public string Name => "Check Connectivity";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var connected = await context.Remote.TestConnectionAsync(cancellationToken);
        if (!connected)
            return StepResult.Fail($"Cannot connect to '{context.TargetServer}' via WinRM. Ensure WinRM is enabled and the operator account has local admin rights.");

        // Confirm local admin — attempt to read a restricted path
        var adminCheck = await context.Remote.ExecuteScriptAsync(
            "$null = [System.Security.Principal.WindowsIdentity]::GetCurrent(); " +
            "$principal = [System.Security.Principal.WindowsPrincipal] [System.Security.Principal.WindowsIdentity]::GetCurrent(); " +
            "$principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)",
            cancellationToken: cancellationToken);

        if (!adminCheck.Success || !adminCheck.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
            return StepResult.Fail($"Connected to '{context.TargetServer}' but the current user does not have local administrator rights.");

        // Check if this is a fresh install or an update — record previous version
        context.PreviousVersion = await DetectInstalledVersionAsync(context, cancellationToken);
        context.IsFreshInstall = context.PreviousVersion is null;

        context.Logger.LogInformation(
            "[{App}@{Server}] Connectivity OK. {InstallState}",
            context.ApplicationName, context.TargetServer,
            context.IsFreshInstall ? "Fresh install (not currently installed)." : $"Currently installed: v{context.PreviousVersion}");

        // For IIS applications whose health check uses HTTPS (and skip-tls-check is not set),
        // verify that the IIS site has a valid, unexpired SSL certificate before we make any changes.
        // Catching this early — before files are deployed, app pools created, etc. — avoids
        // a failed health check at step 11 followed by a full rollback.
        if (context.AppType == ApplicationType.IisApplication)
        {
            var hc = context.Deployment.HealthCheck;
            if (hc is not null &&
                hc.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var siteName = context.ApplicationManifest.Application.Iis!.SiteName;
                var (certOk, certError, certInfo) =
                    await CheckIisSslCertificateAsync(context, siteName, cancellationToken);

                if (!certOk)
                    return StepResult.Fail(
                        $"SSL certificate pre-flight check failed for IIS site '{siteName}' on '{context.TargetServer}': {certError}. " +
                        "Install a valid SSL certificate and bind it to the site in IIS before deploying.");

                context.Logger.LogInformation(
                    "[{App}@{Server}] SSL certificate OK — {CertInfo}",
                    context.ApplicationName, context.TargetServer, certInfo);
            }
        }

        return StepResult.Ok();
    }

    /// <summary>
    /// Checks whether the named IIS site has an HTTPS binding with a valid, unexpired certificate.
    /// Runs on the target server via WinRM.
    /// Returns (true, "", summary) on success; (false, reason, "") on any problem.
    /// </summary>
    private static async Task<(bool Valid, string Error, string Info)> CheckIisSslCertificateAsync(
        DeploymentContext context, string siteName, CancellationToken cancellationToken)
    {
        var script = $@"
$siteName = '{EscapePs(siteName)}'
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue)) {{
    Write-Output 'NO_IIS'; return
}}

$httpsBindings = @(Get-WebBinding -Name $siteName -Protocol 'https' -ErrorAction SilentlyContinue)
if ($httpsBindings.Count -eq 0) {{
    Write-Output 'NO_HTTPS_BINDING'; return
}}

# Inspect the first HTTPS binding (there is typically only one)
$binding  = $httpsBindings[0]
$certHash = $binding.certificateHash

if ([string]::IsNullOrEmpty($certHash)) {{
    Write-Output 'NO_CERT_BOUND'; return
}}

$cert = Get-Item ""Cert:\LocalMachine\My\$certHash"" -ErrorAction SilentlyContinue
if ($null -eq $cert) {{
    Write-Output ""CERT_NOT_FOUND:$certHash""; return
}}

if ($cert.NotAfter -lt [DateTime]::Now) {{
    Write-Output ""CERT_EXPIRED:$($cert.NotAfter.ToString('yyyy-MM-dd'))""; return
}}

$daysLeft = [int](($cert.NotAfter - [DateTime]::Now).TotalDays)
Write-Output ""OK:$($cert.Subject) — expires $($cert.NotAfter.ToString('yyyy-MM-dd')) ($daysLeft days remaining)""
";
        var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);
        if (!result.Success)
            return (false, $"could not query IIS certificate state: {result.Errors.Trim()}", string.Empty);

        var output = result.Output.Trim();
        return output switch
        {
            "NO_IIS" =>
                (false, "WebAdministration module is not available — is IIS Management Scripts and Tools installed?", string.Empty),
            "NO_HTTPS_BINDING" =>
                (false, $"site '{siteName}' has no HTTPS binding in IIS", string.Empty),
            "NO_CERT_BOUND" =>
                (false, $"site '{siteName}' has an HTTPS binding but no certificate is assigned to it", string.Empty),
            _ when output.StartsWith("CERT_NOT_FOUND:") =>
                (false, $"certificate thumbprint '{output["CERT_NOT_FOUND:".Length..]}' is in the binding but missing from the machine certificate store", string.Empty),
            _ when output.StartsWith("CERT_EXPIRED:") =>
                (false, $"certificate expired on {output["CERT_EXPIRED:".Length..]}", string.Empty),
            _ when output.StartsWith("OK:") =>
                (true, string.Empty, output["OK:".Length..]),
            _ =>
                (false, $"unexpected response: {output}", string.Empty)
        };
    }

    private async Task<string?> DetectInstalledVersionAsync(DeploymentContext context, CancellationToken cancellationToken)
    {
        // Version detection: read the .installer-version marker file written during install.
        // This is a lightweight file we create in the install directory on every deployment.
        var versionFilePath = GetVersionFilePath(context);
        if (versionFilePath is null) return null;

        var exists = await context.Remote.FileExistsAsync(versionFilePath, cancellationToken);
        if (!exists) return null;

        try
        {
            var content = await context.Remote.ReadFileAsync(versionFilePath, cancellationToken);
            return content.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetVersionFilePath(DeploymentContext context)
    {
        var app = context.ApplicationManifest.Application;
        return app.Type switch
        {
            ApplicationType.WindowsService when app.Service is not null =>
                Path.Combine(app.Service.InstallDirectory, ".installer-version"),
            ApplicationType.IisApplication when app.Iis is not null =>
                Path.Combine(app.Iis.PhysicalPath, ".installer-version"),
            _ => null
        };
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static string EscapePs(string s) => s.Replace("'", "''");
}
