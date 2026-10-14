using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Adapters;

/// <summary>
/// Manages Windows Services on the target server via sc.exe and PowerShell Service cmdlets.
/// Supports built-in accounts, gMSA (Group Managed Service Accounts), and regular domain accounts.
/// gMSAs use an empty password — AD manages the secret automatically.
/// Regular domain accounts require a password, supplied via <see cref="DeploymentContext.DomainPassword"/>,
/// which is collected once at startup from --password / INSTALLER_DOMAIN_PASSWORD / interactive prompt.
/// </summary>
public sealed class WindowsServiceAdapter : IApplicationAdapter
{
    private readonly ILogger<WindowsServiceAdapter> _logger;

    public WindowsServiceAdapter(ILogger<WindowsServiceAdapter> logger) => _logger = logger;

    public async Task<bool> IsRunningAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var serviceName = context.ApplicationManifest.Application.Service!.Name;
        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            {ServiceLookupPs(serviceName)}
            if ($null -eq $svc) {{ Write-Output 'NotFound' }}
            else {{ Write-Output $svc.Status.ToString() }}
            ",
            cancellationToken: cancellationToken);

        var status = result.Output.Trim();
        return status.Equals("Running", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StopAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var serviceName = context.ApplicationManifest.Application.Service!.Name;

        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            {ServiceLookupPs(serviceName)}
            if ($null -ne $svc -and $svc.Status -eq 'Running') {{
                Stop-Service -Name '{EscapePs(serviceName)}' -Force
                $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
                Write-Output ""Service stopped.""
            }} else {{
                Write-Output ""Service was not running.""
            }}
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Failed to stop service '{serviceName}': {result.Errors}");

        _logger.LogDebug("[{App}@{Server}] Service '{Svc}' stopped.", context.ApplicationName, context.TargetServer, serviceName);
    }

    public async Task StartAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var serviceName = context.ApplicationManifest.Application.Service!.Name;

        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            Start-Service -Name '{EscapePs(serviceName)}'
            $svc = Get-Service -Name '{EscapePs(serviceName)}'
            $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
            Write-Output ""Service started: $($svc.Status)""
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Failed to start service '{serviceName}': {result.Errors}");

        _logger.LogDebug("[{App}@{Server}] Service '{Svc}' started.", context.ApplicationName, context.TargetServer, serviceName);
    }

    public async Task RegisterAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var svc     = context.ApplicationManifest.Application.Service!;
        var account = context.EffectiveAccount;  // resolved: per-app → env default → built-in fallback

        var installDir = context.EffectiveInstallDirectory;
        var binaryPath = await ResolveExecutablePathAsync(context, svc, installDir, cancellationToken);
        if (!string.IsNullOrWhiteSpace(svc.BinaryPathSuffix))
            binaryPath += $" {svc.BinaryPathSuffix}";

        var startTypeMap = svc.StartType.ToLowerInvariant() switch
        {
            "manual"                                  => "demand",
            "disabled"                                => "disabled",
            "automatic" or "auto"                     => "auto",
            // All other values (including the default "automatic-delayed") → delayed-auto.
            // "Automatic (Delayed Start)" is preferred for services: it avoids competing
            // with OS services at boot and reduces login-time latency.
            _                                         => "delayed-auto"
        };

        // For regular domain accounts, ensure SeServiceLogonRight ("Log on as a service")
        // is granted before sc.exe create/config — a service whose account lacks this right
        // will be registered successfully but fail immediately on first start.
        // Built-in accounts (LocalSystem, NetworkService, LocalService) and gMSAs are exempt:
        // built-ins already have the right, and AD manages gMSA logon rights automatically.
        if (!account.IsBuiltIn && !account.IsGmsa)
        {
            var logonScript = BuildEnsureLogonRightScript(account);
            var logonResult = await context.Remote.ExecuteScriptAsync(logonScript, cancellationToken: cancellationToken);
            if (!logonResult.Success)
                throw new InvalidOperationException(
                    $"Cannot grant SeServiceLogonRight to '{account.AccountName}': {logonResult.Errors}\n" +
                    "Fix manually: Local Security Policy → Local Policies → User Rights Assignment → Log on as a service.");
            if (!string.IsNullOrWhiteSpace(logonResult.Output))
                _logger.LogInformation("[{App}@{Server}] {Out}",
                    context.ApplicationName, context.TargetServer, logonResult.Output.Trim());
        }

        // Check if service already exists
        var existsResult = await context.Remote.ExecuteScriptAsync(
            $"$null -ne {ServiceExistsPs(svc.Name)}",
            cancellationToken: cancellationToken);
        var serviceExists = existsResult.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

        if (!serviceExists)
        {
            // Create new service.
            // gMSA accounts use an empty password; regular domain accounts use context.DomainPassword.
            var createScript = BuildCreateScript(svc, account, binaryPath, startTypeMap, context.DomainPassword);
            var result = await context.Remote.ExecuteScriptAsync(createScript, cancellationToken: cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Service creation failed: {result.Errors}");

            _logger.LogInformation("[{App}@{Server}] Service '{Svc}' created.", context.ApplicationName, context.TargetServer, svc.Name);
        }
        else
        {
            // Update existing service — change binary path, start type, and identity.
            var updateScript = BuildUpdateScript(svc, account, binaryPath, startTypeMap, context.DomainPassword);
            var result = await context.Remote.ExecuteScriptAsync(updateScript, cancellationToken: cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Service update failed: {result.Errors}");

            _logger.LogInformation("[{App}@{Server}] Service '{Svc}' updated.", context.ApplicationName, context.TargetServer, svc.Name);
        }

        // Update description (sc.exe does not support description in create/config)
        if (!string.IsNullOrWhiteSpace(svc.Description))
        {
            await context.Remote.ExecuteScriptAsync(
                $"sc.exe description '{EscapePs(svc.Name)}' '{EscapePs(svc.Description)}'",
                cancellationToken: cancellationToken);
        }

        // Grant the service account read/execute on the install directory.
        // LocalSystem (SYSTEM) already has full access; all others need an explicit grant
        // because robocopy uses /COPY:DAT and does not propagate source ACLs.
        if (!account.IsLocalSystem) // LocalSystem has implicit full access; all others need explicit grant
        {
            // Values go into single-quoted variables and are referenced as ${var}: gMSA names end
            // with '$', which PowerShell would parse as a variable reference inside "...".
            var aclScript =
                $"$installDir = '{EscapePs(installDir)}'\n" +
                $"$aclAccount = '{EscapePs(account.AccountName)}'\n" +
                $"icacls \"${{installDir}}\" /grant \"${{aclAccount}}:(OI)(CI)RX\" /T /Q 2>$null | Out-Null\n" +
                $"if ($LASTEXITCODE -ne 0) {{ Write-Warning \"icacls grant returned exit $LASTEXITCODE\" }}\n" +
                $"else {{ Write-Output \"File permissions set: ${{aclAccount}} has ReadAndExecute\" }}";
            var aclResult = await context.Remote.ExecuteScriptAsync(aclScript, cancellationToken: cancellationToken);
            if (!aclResult.Success)
                _logger.LogWarning(
                    "[{App}@{Server}] Could not grant '{Account}' read/execute on '{Dir}': {Error}",
                    context.ApplicationName, context.TargetServer, account.AccountName, installDir, aclResult.Errors);
            else if (!string.IsNullOrWhiteSpace(aclResult.Output))
                _logger.LogInformation("[{App}@{Server}] {AclOut}", context.ApplicationName, context.TargetServer, aclResult.Output.Trim());
        }
    }

    /// <summary>
    /// Finds the service executable in the deployed install directory.
    /// Packages do not consistently name the exe after the service (e.g. a service called
    /// <c>Elements.Foo</c> may ship <c>FooService.exe</c>), so the name is not assumed:
    /// <list type="bullet">
    ///   <item>One .exe → that file.</item>
    ///   <item>Several → the one named after the service, otherwise an error listing them.</item>
    ///   <item>None → falls back to <c>{service name}.exe</c> with a warning.</item>
    /// </list>
    /// Chocolatey helper executables are ignored. Top-level files are preferred over nested ones.
    /// </summary>
    private async Task<string> ResolveExecutablePathAsync(
        DeploymentContext context, ServiceConfig svc, string installDir, CancellationToken cancellationToken)
    {
        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            $dir = '{EscapePs(installDir)}'
            $exes = @(Get-ChildItem -LiteralPath $dir -Filter *.exe -File -ErrorAction SilentlyContinue)
            if ($exes.Count -eq 0) {{
                $exes = @(Get-ChildItem -LiteralPath $dir -Filter *.exe -File -Recurse -ErrorAction SilentlyContinue |
                          Sort-Object {{ $_.FullName.Split('\').Count }})
            }}
            $exes | Where-Object {{ $_.Name -notlike 'chocolatey*' -and $_.Name -ne 'shimgen.exe' }} |
                ForEach-Object {{ $_.FullName }}
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Could not look for the service executable in '{installDir}': {result.Errors}");

        var exes = result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        string? chosen = exes.Count == 1
            ? exes[0]
            : exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e)
                .Equals(svc.Name, StringComparison.OrdinalIgnoreCase));

        if (chosen is null && exes.Count > 1)
            throw new InvalidOperationException(
                $"Found {exes.Count} executables in '{installDir}' and none is named after service " +
                $"'{svc.Name}', so the service executable is ambiguous: {string.Join(", ", exes.Select(Path.GetFileName))}");

        if (chosen is null)
        {
            var fallback = Path.Combine(installDir, $"{svc.Name}.exe");
            _logger.LogWarning(
                "[{App}@{Server}] No .exe found in '{Dir}'; using '{Fallback}' as the service binary.",
                context.ApplicationName, context.TargetServer, installDir, fallback);
            return fallback;
        }

        _logger.LogDebug("[{App}@{Server}] Service executable: {Exe}",
            context.ApplicationName, context.TargetServer, chosen);
        return chosen;
    }

    public async Task UnregisterAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var serviceName = context.ApplicationManifest.Application.Service!.Name;

        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            {ServiceLookupPs(serviceName)}
            if ($null -ne $svc) {{
                if ($svc.Status -eq 'Running') {{
                    Stop-Service -Name '{EscapePs(serviceName)}' -Force
                    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
                }}
                sc.exe delete '{EscapePs(serviceName)}'
                Write-Output ""Service deleted.""
            }} else {{
                Write-Output ""Service was not installed.""
            }}
            ",
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Service deletion failed: {result.Errors}");
    }

    public async Task<string> GetStatusAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var serviceName = context.ApplicationManifest.Application.Service!.Name;
        var result = await context.Remote.ExecuteScriptAsync(
            $@"
            {ServiceLookupPs(serviceName)}
            if ($null -eq $svc) {{ Write-Output 'Not installed' }}
            else {{
                $identity = (Get-WmiObject Win32_Service -Filter ""Name='{EscapePs(serviceName)}'"").StartName
                Write-Output ""$($svc.Status) (Identity: $identity)""
            }}
            ",
            cancellationToken: cancellationToken);
        return result.Success ? result.Output.Trim() : $"Unknown (error: {result.Errors})";
    }

    /// <summary>
    /// Builds the obj=/password= fragment for sc.exe create/config.
    /// <list type="bullet">
    ///   <item>Built-in account  → no obj= needed.</item>
    ///   <item>gMSA (ends with '$') → empty password; Windows retrieves it from AD.</item>
    ///   <item>Regular domain account → use <paramref name="domainPassword"/>.</item>
    /// </list>
    /// The returned string uses literal double-quotes, suitable for the --% stop-parsing context.
    /// </summary>
    private static string BuildObjParam(ServiceAccount account, string? domainPassword)
    {
        if (account.IsBuiltIn) return string.Empty;
        var pw = account.IsGmsa ? string.Empty : (domainPassword ?? string.Empty);
        // Literal " chars here — correct for the sc.exe --% raw command-line context.
        return $@"obj= ""{EscapeCmd(account.AccountName)}"" password= ""{EscapeCmd(pw)}""";
    }

    private static string BuildCreateScript(
        ServiceConfig svc, ServiceAccount account, string binaryPath, string startType, string? domainPassword)
    {
        var objParam  = BuildObjParam(account, domainPassword);
        // Trailing space before DisplayName= is intentional: when objParam is empty the
        // extra space is harmless; when it is populated the space separates the two fragments.
        var objClause = objParam.Length > 0 ? $" {objParam}" : string.Empty;

        // sc.exe uses its own command-line parser and requires a literal space between
        // each "key=" and its value.  PowerShell's argument-passing layer can mangle this
        // in a WinRM runspace.  The --% (stop-parsing) token bypasses PowerShell entirely:
        // everything that follows is forwarded verbatim to sc.exe.
        // All substitution is done here in C# before the script reaches PowerShell.
        return $@"
$scOut = sc.exe --% create ""{EscapeCmd(svc.Name)}"" binPath= ""{EscapeCmd(binaryPath)}"" start= {startType}{objClause} DisplayName= ""{EscapeCmd(svc.DisplayName)}""
Write-Output ""sc create: $($scOut -join '; ')""
if ($LASTEXITCODE -ne 0) {{
    throw ""sc.exe create failed (exit $LASTEXITCODE): $($scOut -join '; ')""
}}
";
    }

    private static string BuildUpdateScript(
        ServiceConfig svc, ServiceAccount account, string binaryPath, string startType, string? domainPassword)
    {
        var objParam  = BuildObjParam(account, domainPassword);
        var objClause = objParam.Length > 0 ? $" {objParam}" : string.Empty;

        return $@"
$scOut = sc.exe --% config ""{EscapeCmd(svc.Name)}"" binPath= ""{EscapeCmd(binaryPath)}"" start= {startType}{objClause} DisplayName= ""{EscapeCmd(svc.DisplayName)}""
Write-Output ""sc config: $($scOut -join '; ')""
if ($LASTEXITCODE -ne 0) {{
    throw ""sc.exe config failed (exit $LASTEXITCODE): $($scOut -join '; ')""
}}
";
    }

    /// <summary>
    /// Generates a PowerShell script that checks whether <paramref name="account"/> holds the
    /// "Log on as a service" privilege (SeServiceLogonRight) on the target machine and, if not,
    /// grants it via secedit.  The script throws on failure so the caller can surface a clear
    /// error before sc.exe create/config is even attempted.
    /// </summary>
    private static string BuildEnsureLogonRightScript(ServiceAccount account)
    {
        return $@"
$acct = '{EscapePs(account.AccountName)}'

# Translate the account name to a SID — secedit uses the *S-1-... format.
try {{
    $sid = ([System.Security.Principal.NTAccount]$acct).Translate([System.Security.Principal.SecurityIdentifier]).Value
}} catch {{
    throw ""Cannot resolve account '$acct' to a SID — verify the account exists in AD. Detail: $_""
}}

$tmpCfg = [IO.Path]::Combine([IO.Path]::GetTempPath(), ""ins_logon_$PID.cfg"")
$tmpDb  = [IO.Path]::Combine([IO.Path]::GetTempPath(), ""ins_logon_$PID.sdb"")
try {{
    # Export the current local security policy (secedit produces a UTF-16 .inf file).
    $null = & secedit.exe /export /cfg $tmpCfg /quiet
    $lines = Get-Content $tmpCfg -Encoding Unicode
    $entry = ""*$sid""

    $rightLine = $lines | Where-Object {{ $_ -match '^SeServiceLogonRight\s*=' }} | Select-Object -First 1
    if ($rightLine -and ($rightLine -match [regex]::Escape($entry))) {{
        Write-Output ""SeServiceLogonRight already granted to '$acct'.""
    }} else {{
        if ($rightLine) {{
            # Append the SID to the existing SeServiceLogonRight entry.
            $lines = $lines -replace '^(SeServiceLogonRight\s*=.*)', ""`$1,$entry""
        }} else {{
            # No SeServiceLogonRight line at all — insert one under [Privilege Rights].
            $pivot = $lines | Where-Object {{ $_ -match '^\[Privilege Rights\]' }} | Select-Object -First 1
            if ($null -eq $pivot) {{
                throw ""[Privilege Rights] section not found in secedit export — cannot grant SeServiceLogonRight.""
            }}
            $idx   = [array]::IndexOf([string[]]$lines, $pivot)
            $lines = @($lines[0..$idx]) + ""SeServiceLogonRight = $entry"" + @($lines[($idx + 1)..($lines.Length - 1)])
        }}
        $lines | Set-Content $tmpCfg -Encoding Unicode
        $out = & secedit.exe /configure /db $tmpDb /cfg $tmpCfg /areas USER_RIGHTS /quiet 2>&1
        if ($LASTEXITCODE -ne 0) {{
            throw ""secedit failed (exit $LASTEXITCODE) granting SeServiceLogonRight to '$acct': $($out -join '; ')""
        }}
        Write-Output ""Granted SeServiceLogonRight to '$acct'.""
    }}
}} finally {{
    Remove-Item $tmpCfg, $tmpDb -Force -ErrorAction SilentlyContinue
}}
";
    }

    // Service lookup without Get-Service's "not found" error. In a hosted runspace that error marks
    // the whole script as failed (HadErrors) even with -ErrorAction SilentlyContinue/Ignore, which
    // turned "service not installed" into an error. WMI returns $null for a missing service, and
    // Get-Service (which then cannot fail) is only called when the service exists.
    // The filter is built by concatenation so the name never sits inside a double-quoted string.
    private static string ServiceExistsPs(string name) =>
        $@"(Get-WmiObject Win32_Service -Filter (""Name='"" + '{EscapePs(name)}' + ""'""))";

    private static string ServiceLookupPs(string name) =>
        $@"$svc = if ({ServiceExistsPs(name)}) {{ Get-Service -Name '{EscapePs(name)}' }} else {{ $null }}";

    private static string EscapePs(string s) => s.Replace("'", "''").Replace("\"", "`\"");

    /// <summary>
    /// Escapes a value for embedding in an sc.exe raw command line (--% context).
    /// Double-quote is escaped by doubling ("") per Windows command-line conventions.
    /// Backslashes are never special outside quotes in sc.exe's parser.
    /// </summary>
    private static string EscapeCmd(string s) => s.Replace("\"", "\"\"");
}
