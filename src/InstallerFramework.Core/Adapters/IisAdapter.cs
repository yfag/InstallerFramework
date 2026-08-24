using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Adapters;

/// <summary>
/// Manages IIS applications on the target server via WinRM.
///
/// Register / Unregister use appcmd.exe (%SystemRoot%\system32\inetsrv\appcmd.exe), which
/// is always present when IIS is installed and avoids the ArgumentNullException("path") bug
/// triggered by WebAdministration's Set-ItemProperty when managedRuntimeVersion is empty
/// (No Managed Code / .NET Core).
///
/// Stop / Start / IsRunning / GetStatus continue to use the WebAdministration module, which
/// is reliable for reading pool state and starting/stopping pools.
///
/// Handles two hosting models transparently:
///   DotNetRuntime.Core      — app pool CLR = "" (No Managed Code), web.config shipped in package
///   DotNetRuntime.Framework — app pool CLR = "v4.0", classic .NET Framework hosting
///
/// For gMSA app pool identities: userName = "DOMAIN\svc-account$", password = ""
/// Windows resolves the gMSA password automatically — no password ever stored or prompted.
/// </summary>
public sealed class IisAdapter : IApplicationAdapter
{
    private readonly ILogger<IisAdapter> _logger;

    public IisAdapter(ILogger<IisAdapter> logger) => _logger = logger;

    public async Task<bool> IsRunningAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var poolName = context.ApplicationManifest.Application.Iis!.AppPool.Name;
        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $pool = Get-Item 'IIS:\AppPools\{EscapePs(poolName)}' -ErrorAction SilentlyContinue
            if ($null -eq $pool) {{ Write-Output 'NotFound' }}
            else {{ Write-Output $pool.State.ToString() }}
            ",
            cancellationToken: cancellationToken);

        return result.Output.Trim().Equals("Started", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StopAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var poolName = context.ApplicationManifest.Application.Iis!.AppPool.Name;

        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $pool = Get-Item 'IIS:\AppPools\{EscapePs(poolName)}' -ErrorAction SilentlyContinue
            if ($null -ne $pool -and $pool.State -eq 'Started') {{
                Stop-WebAppPool -Name '{EscapePs(poolName)}'
                # Wait for pool to stop (max 30s). Re-fetch each iteration — older WebAdministration
                # versions return ConfigurationElement which has no Refresh() method.
                $timeout = [DateTime]::Now.AddSeconds(30)
                do {{
                    Start-Sleep -Milliseconds 500
                    $pool = Get-Item 'IIS:\AppPools\{EscapePs(poolName)}' -ErrorAction SilentlyContinue
                }} while ($pool.State -ne 'Stopped' -and [DateTime]::Now -lt $timeout)
                Write-Output ""App pool stopped: $($pool.State)""
            }} else {{
                Write-Output ""App pool was not running.""
            }}
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Failed to stop app pool '{poolName}': {result.Errors}");

        _logger.LogDebug("[{App}@{Server}] App pool '{Pool}' stopped.", context.ApplicationName, context.TargetServer, poolName);
    }

    public async Task StartAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var poolName = context.ApplicationManifest.Application.Iis!.AppPool.Name;

        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            Start-WebAppPool -Name '{EscapePs(poolName)}'
            # Re-fetch each iteration — older WebAdministration returns ConfigurationElement
            # which has no Refresh() method.
            $timeout = [DateTime]::Now.AddSeconds(30)
            do {{
                Start-Sleep -Milliseconds 500
                $pool = Get-Item 'IIS:\AppPools\{EscapePs(poolName)}'
            }} while ($pool.State -ne 'Started' -and [DateTime]::Now -lt $timeout)
            Write-Output ""App pool state: $($pool.State)""
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Failed to start app pool '{poolName}': {result.Errors}");

        _logger.LogDebug("[{App}@{Server}] App pool '{Pool}' started.", context.ApplicationName, context.TargetServer, poolName);
    }

    public async Task RegisterAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var iis     = context.ApplicationManifest.Application.Iis!;
        var pool    = iis.AppPool;
        var account = context.EffectiveAccount;  // resolved: per-app → env default → built-in fallback

        // CLR version: empty string = No Managed Code (.NET Core), "v4.0" = .NET Framework
        var clrVersion = iis.Runtime == DotNetRuntime.Core ? string.Empty : "v4.0";

        // Identity type for appcmd
        var (identityType, userName, password) = ResolveIdentity(account, context.DomainPassword);
        var identityTypeStr = identityType switch
        {
            0 => "LocalSystem",
            1 => "LocalService",
            2 => "NetworkService",
            3 => "SpecificUser",
            _ => "ApplicationPoolIdentity"
        };

        // idleTimeout in HH:MM:SS format expected by appcmd
        var idleTimeoutStr = pool.IdleTimeoutMinutes == 0
            ? "00:00:00"
            : $"{pool.IdleTimeoutMinutes / 60:D2}:{pool.IdleTimeoutMinutes % 60:D2}:00";

        // Application name without leading slash (appcmd /path: uses /name, site uses "Site/name")
        var appName = iis.ApplicationPath.TrimStart('/');

        // Build the icacls grant fragment for the app pool identity.
        // (OI)(CI)RX = Object+Container Inherit, Read+Execute — minimum required for IIS.
        // LocalSystem (0) already has full access; no explicit grant needed.
        var aclAccount = identityType switch
        {
            0 => null,
            1 => "NT AUTHORITY\\LOCAL SERVICE",
            2 => "NT AUTHORITY\\NETWORK SERVICE",
            3 => userName,                          // specific domain account
            _ => $"IIS AppPool\\{pool.Name}"        // ApplicationPoolIdentity virtual account
        };
        var aclGrantPs = aclAccount is null ? string.Empty :
            $"icacls \"$physPath\" /grant \"{aclAccount}:(OI)(CI)RX\" /T /Q 2>$null | Out-Null\n" +
            $"if ($LASTEXITCODE -ne 0) {{ Write-Warning \"icacls grant returned exit $LASTEXITCODE\" }}\n" +
            $"else {{ Write-Output \"File permissions set: {aclAccount} has ReadAndExecute on $physPath\" }}";

        // ASPNETCORE_ENVIRONMENT must be set on the app pool so the worker process loads the
        // correct appsettings.{env}.json overlay at runtime.  We only do this for .NET Core
        // pools (Framework apps use a different config system and don't need it).
        // Value = overlay suffix from the app manifest (e.g. "Production"); fallback "Production".
        var aspNetCoreEnv = iis.Runtime == DotNetRuntime.Core
            ? (!string.IsNullOrWhiteSpace(context.ApplicationManifest.Application.Configuration.OverlaySuffix)
                ? context.ApplicationManifest.Application.Configuration.OverlaySuffix
                : "Production")
            : null;

        // PowerShell fragment: idempotent — removes any existing entry then adds the correct one.
        // Wrapped in try/catch so a non-fatal IIS API hiccup doesn't abort the whole deployment.
        var aspNetCoreEnvPs = aspNetCoreEnv is null ? string.Empty :
            $@"# --- ASP.NET Core environment variable ---
# ASPNETCORE_ENVIRONMENT tells ASP.NET Core which appsettings.{{env}}.json overlay to load.
# Requires IIS 10+ (Windows Server 2016+) which supports per-pool environment variables.
try {{
    # Clear-WebConfiguration targets the specific collection entry by name — the reliable
    # way to remove an IIS collection item in PS 5.1 (unlike calling .delete() on the proxy object).
    Clear-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Filter ""system.applicationHost/applicationPools/add[@name='$poolName']/environmentVariables/add[@name='ASPNETCORE_ENVIRONMENT']"" `
        -ErrorAction SilentlyContinue
    Add-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' `
        -Filter ""system.applicationHost/applicationPools/add[@name='$poolName']/environmentVariables"" `
        -Value @{{name='ASPNETCORE_ENVIRONMENT'; value='{EscapePs(aspNetCoreEnv)}'}}
    Write-Output ""ASPNETCORE_ENVIRONMENT='{EscapePs(aspNetCoreEnv)}' set on app pool '$poolName'""
}} catch {{
    Write-Warning ""Could not set ASPNETCORE_ENVIRONMENT on app pool (non-fatal): $_""
}}";

        var script = $@"
$ErrorActionPreference = 'Stop'

$appcmd   = ""$env:SystemRoot\system32\inetsrv\appcmd.exe""
$poolName = '{EscapePs(pool.Name)}'
$siteName = '{EscapePs(iis.SiteName)}'
$appName  = '{EscapePs(appName)}'
$physPath = '{EscapePs(iis.PhysicalPath)}'
$clrVer   = '{EscapePs(clrVersion)}'

if (-not (Test-Path $appcmd)) {{
    throw 'appcmd.exe not found at ' + $appcmd + '. IIS may not be installed correctly.'
}}

# --- App Pool ---
# list exits 0 (found) or 1 (not found) — 1 is normal/expected, not an error.
# Positional arg form; 2>$null avoids ErrorActionPreference=Stop treating stderr as terminating.
$poolInfo = & $appcmd list apppool ""$poolName"" 2>$null

if ($poolInfo) {{
    Write-Output ""App pool exists, updating: $poolName""
    & $appcmd set apppool ""$poolName"" `
        ""/managedRuntimeVersion:$clrVer"" `
        '/managedPipelineMode:{EscapePs(pool.PipelineMode)}' `
        '/processModel.identityType:{identityTypeStr}' `
        '/autoStart:{pool.AutoStart.ToString().ToLower()}' `
        '/startMode:{EscapePs(pool.StartMode)}' `
        '/processModel.idleTimeout:{idleTimeoutStr}'
    if ($LASTEXITCODE -ne 0) {{ throw ""appcmd set apppool failed (exit $LASTEXITCODE)"" }}
}} else {{
    Write-Output ""Creating app pool: $poolName""
    & $appcmd add apppool ""/name:$poolName"" `
        ""/managedRuntimeVersion:$clrVer"" `
        '/managedPipelineMode:{EscapePs(pool.PipelineMode)}' `
        '/processModel.identityType:{identityTypeStr}' `
        '/autoStart:{pool.AutoStart.ToString().ToLower()}' `
        '/startMode:{EscapePs(pool.StartMode)}' `
        '/processModel.idleTimeout:{idleTimeoutStr}'
    if ($LASTEXITCODE -ne 0) {{ throw ""appcmd add apppool failed (exit $LASTEXITCODE)"" }}
    Write-Output ""App pool created: $poolName""
}}
{(identityType == 3 ? $@"& $appcmd set apppool '{EscapePs(pool.Name)}' ""/processModel.userName:{EscapePs(userName)}"" ""/processModel.password:{EscapePs(password)}""
if ($LASTEXITCODE -ne 0) {{ throw 'appcmd set apppool identity failed (exit ' + $LASTEXITCODE + ')' }}" : string.Empty)}

# --- Web Application (WebAdministration) ---
# appcmd cannot modify apps when IIS has delegated config or overrideMode=Deny at the site level.
# WebAdministration uses a different config path and works correctly for app registration.
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue)) {{
    throw 'WebAdministration module could not be loaded. Ensure the IIS Management Scripts and Tools feature is installed.'
}}

New-Item -ItemType Directory -Path $physPath -Force | Out-Null
$fullPath = ""IIS:\Sites\$siteName\$appName""

# Always Remove + recreate — avoids Set-ItemProperty on app nodes, which throws
# ArgumentNullException('path') in WebAdministration when the pool managedRuntimeVersion
# was previously set to empty string (No Managed Code).
if (Test-Path $fullPath) {{
    Remove-WebApplication -Name $appName -Site $siteName
    Write-Output ""Removed existing web application: $siteName/$appName""
}}
New-WebApplication -Name $appName -Site $siteName -PhysicalPath $physPath -ApplicationPool $poolName | Out-Null
Write-Output ""Web application created: $siteName/$appName""

# --- File system permissions ---
# Grant the app pool identity read/execute on the physical path.
# This is always required: robocopy uses /COPY:DAT (no ACL propagation), so files
# inherit from the parent directory but the pool identity still needs an explicit grant
# when running as a specific user or virtual ApplicationPoolIdentity account.
{aclGrantPs}

{aspNetCoreEnvPs}
";

        var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"IIS registration failed: {result.Errors}");

        if (!string.IsNullOrWhiteSpace(result.Output))
            _logger.LogInformation("[{App}@{Server}] {Output}", context.ApplicationName, context.TargetServer, result.Output.Trim());
    }

    public async Task UnregisterAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var iis     = context.ApplicationManifest.Application.Iis!;
        var appName = iis.ApplicationPath.TrimStart('/');

        var script = $@"
$ErrorActionPreference = 'Stop'

$appcmd   = ""$env:SystemRoot\system32\inetsrv\appcmd.exe""
$siteName = '{EscapePs(iis.SiteName)}'
$appName  = '{EscapePs(appName)}'
$poolName = '{EscapePs(iis.AppPool.Name)}'

# --- Remove web application (WebAdministration) ---
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue) {{
    $fullPath = ""IIS:\Sites\$siteName\$appName""
    if (Test-Path $fullPath) {{
        Remove-WebApplication -Name $appName -Site $siteName
        Write-Output ""Web application removed: $siteName/$appName""
    }}
}}

# --- Remove app pool (appcmd) ---
if (Test-Path $appcmd) {{
    $poolInfo = & $appcmd list apppool ""$poolName"" 2>$null
    if ($poolInfo) {{
        & $appcmd stop apppool ""$poolName"" 2>$null | Out-Null
        & $appcmd delete apppool ""$poolName""
        if ($LASTEXITCODE -ne 0) {{ throw ""appcmd delete apppool failed (exit $LASTEXITCODE)"" }}
        Write-Output ""App pool removed: $poolName""
    }}
}}
";

        var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"IIS unregistration failed: {result.Errors}");
    }

    public async Task<string> GetStatusAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var iis = context.ApplicationManifest.Application.Iis!;
        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $appName  = '{EscapePs(iis.ApplicationPath)}'.TrimStart('/')
            $poolPath = 'IIS:\AppPools\{EscapePs(iis.AppPool.Name)}'
            $appPath  = ""IIS:\Sites\{EscapePs(iis.SiteName)}\$appName""

            $poolState = if (Test-Path $poolPath) {{ (Get-Item $poolPath).State }} else {{ 'Not installed' }}
            $appExists  = Test-Path $appPath

            $identity = if (Test-Path $poolPath) {{
                (Get-ItemProperty $poolPath -Name processModel).processModel.userName
            }} else {{ 'N/A' }}

            Write-Output ""Pool: $poolState | App: $(if ($appExists) {{ 'Registered' }} else {{ 'Not registered' }}) | Identity: $identity""
            ",
            cancellationToken: cancellationToken);

        return result.Success ? result.Output.Trim() : $"Unknown (error: {result.Errors})";
    }

    /// <summary>
    /// Maps a ServiceAccount to the IIS identity type integer and credentials.
    /// <list type="bullet">
    ///   <item>Built-in accounts (LocalSystem, LocalService, NetworkService, ApplicationPoolIdentity) — no user/password.</item>
    ///   <item>gMSA (account name ends with '$') — SpecificUser with empty password; AD manages the secret.</item>
    ///   <item>Regular domain account — SpecificUser with the supplied <paramref name="domainPassword"/>.</item>
    /// </list>
    /// </summary>
    private static (int IdentityType, string UserName, string Password) ResolveIdentity(
        ServiceAccount account, string? domainPassword)
    {
        if (account.IsLocalSystem)            return (0, string.Empty, string.Empty);
        if (account.IsLocalService)           return (1, string.Empty, string.Empty);
        if (account.IsNetworkService)         return (2, string.Empty, string.Empty);
        if (account.IsApplicationPoolIdentity) return (4, string.Empty, string.Empty);

        // SpecificUser — gMSA or regular domain account
        // gMSA:    password is always empty; Windows retrieves it from AD automatically
        // Regular: use the password provided at startup
        var pw = account.IsGmsa ? string.Empty : (domainPassword ?? string.Empty);
        return (3, account.AccountName, pw);
    }

    private static string EscapePs(string s) => s.Replace("'", "''");
}
