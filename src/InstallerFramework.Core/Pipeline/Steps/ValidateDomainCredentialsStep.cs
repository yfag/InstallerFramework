using InstallerFramework.Core.Models;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 1 — Pre-flight.
/// Validates the supplied domain password before any server-side changes are made.
///
/// Runs only when:
///   • A domain password has been provided (<see cref="DeploymentContext.DomainPassword"/> is set), AND
///   • The application's service/pool account is a regular domain account
///     (built-in accounts and gMSAs do not use the password and are skipped).
///
/// Uses <c>PrincipalContext.ValidateCredentials</c> on the remote server so the
/// check runs in the same domain context as the service will eventually use.
/// Fails fast with a clear error rather than letting a wrong password surface
/// as a cryptic "service failed to start" error later in the pipeline.
///
/// No rollback needed — this step makes no changes.
/// </summary>
public sealed class ValidateDomainCredentialsStep : IDeploymentStep
{
    public string Name => "Validate Domain Credentials";

    public async Task<StepResult> ExecuteAsync(
        DeploymentContext context,
        CancellationToken cancellationToken = default)
    {
        // Nothing to validate if no password was supplied.
        if (string.IsNullOrEmpty(context.DomainPassword))
            return StepResult.Ok();

        // Use the resolved account from the context (per-app override → env default → built-in fallback).
        var account = context.EffectiveAccount;

        // Built-in accounts (LocalSystem, NetworkService…) and gMSAs don't use the password.
        if (account.IsBuiltIn || account.IsGmsa)
            return StepResult.Ok();

        var accountName = account.AccountName;

        // Expect DOMAIN\username format.
        var backslash = accountName.IndexOf('\\');
        if (backslash <= 0 || backslash == accountName.Length - 1)
        {
            context.Logger.LogWarning(
                "[{App}@{Server}] Cannot validate credentials: '{Account}' is not in DOMAIN\\user format — skipping check.",
                context.ApplicationName, context.TargetServer, accountName);
            return StepResult.Ok();
        }

        var domain   = accountName[..backslash];
        var username = accountName[(backslash + 1)..];

        context.Logger.LogInformation(
            "[{App}@{Server}] Validating domain credentials for '{Account}'…",
            context.ApplicationName, context.TargetServer, accountName);

        var script = BuildValidateScript(domain, username, context.DomainPassword);
        var result = await context.Remote.ExecuteScriptAsync(script, cancellationToken: cancellationToken);

        if (!result.Success)
            return StepResult.Fail(
                $"Domain credential validation failed for '{accountName}': {result.Errors}\n" +
                "Check the --password argument or the INSTALLER_DOMAIN_PASSWORD environment variable.");

        context.Logger.LogInformation(
            "[{App}@{Server}] Domain credentials for '{Account}' are valid.",
            context.ApplicationName, context.TargetServer, accountName);

        return StepResult.Ok();
    }

    public Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // Pre-flight step — nothing to undo.

    // -------------------------------------------------------------------------

    private static string BuildValidateScript(string domain, string username, string password)
    {
        // Values are embedded in PS single-quoted strings; only ' needs escaping.
        var d = EscapePs(domain);
        var u = EscapePs(username);
        var p = EscapePs(password);

        // PrincipalContext.ValidateCredentials performs a real LDAP bind against the DC.
        // Running it on the remote server avoids the Kerberos double-hop problem and uses
        // the same domain network context that the service account will run in.
        return $@"
Add-Type -AssemblyName System.DirectoryServices.AccountManagement
$ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext(
    [System.DirectoryServices.AccountManagement.ContextType]::Domain, '{d}')
try {{
    $valid = $ctx.ValidateCredentials('{u}', '{p}')
    if (-not $valid) {{
        throw ""The password supplied for '{d}\{u}' is incorrect.""
    }}
}} finally {{
    $ctx.Dispose()
}}
";
    }

    /// <summary>Escapes a value for use inside a PowerShell single-quoted string.</summary>
    private static string EscapePs(string s) => s.Replace("'", "''");
}
