namespace InstallerFramework.Core.Models;

/// <summary>
/// Represents an AD account used to run a Windows Service or IIS Application Pool.
/// Supports regular AD accounts, gMSAs, and built-in identities.
/// Passwords are NEVER stored — gMSAs need none, built-ins need none,
/// and regular AD service accounts must be pre-granted "Log on as a service".
/// </summary>
public sealed record ServiceAccount
{
    public string AccountName { get; init; } = string.Empty;

    /// <summary>
    /// gMSA accounts end with '$'. Password is managed entirely by AD.
    /// Service/app pool identity is set to the account name with an empty password string.
    /// </summary>
    public bool IsGmsa => AccountName.TrimEnd().EndsWith('$');

    public bool IsLocalSystem =>
        AccountName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals(".\\LocalSystem", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("NT AUTHORITY\\System", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("System", StringComparison.OrdinalIgnoreCase);

    public bool IsNetworkService =>
        AccountName.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("NT AUTHORITY\\Network Service", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalService =>
        AccountName.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase) ||
        AccountName.Equals("NT AUTHORITY\\Local Service", StringComparison.OrdinalIgnoreCase);

    public bool IsApplicationPoolIdentity =>
        AccountName.Equals("ApplicationPoolIdentity", StringComparison.OrdinalIgnoreCase);

    public bool IsBuiltIn => IsLocalSystem || IsNetworkService || IsLocalService || IsApplicationPoolIdentity;

    /// <summary>
    /// Returns the account name safe for logging (never log passwords — there are none here).
    /// </summary>
    public override string ToString() => AccountName;

    public static ServiceAccount LocalSystem => new() { AccountName = "LocalSystem" };
    public static ServiceAccount NetworkService => new() { AccountName = "NetworkService" };
    public static ServiceAccount ApplicationPoolIdentity => new() { AccountName = "ApplicationPoolIdentity" };
    public static ServiceAccount FromName(string accountName) => new() { AccountName = accountName };
}
