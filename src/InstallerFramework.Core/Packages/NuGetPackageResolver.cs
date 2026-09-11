using NuGet.Versioning;

namespace InstallerFramework.Core.Packages;

/// <summary>
/// Resolves .nupkg files from a local directory or UNC path.
/// Fully offline — no NuGet.org or feed connectivity.
///
/// Handles the naming convention used in the Elements bundle:
///   {PackageId}.{Version}.nupkg
/// where both the package ID and version may contain dots, e.g.:
///   Elements.AddressProviders.Enhetsregisteret.1.18.1.nupkg
///   DocumentDeliveryService.3.14.0.89-26-gfb4a4b8.nupkg
///   nCoreEis.8.0.0-2026040901.nupkg
///
/// Parsing rule (NuGet convention): the version starts at the first
/// dot-separated segment that begins with a digit.
/// </summary>
public sealed class NuGetPackageResolver
{
    /// <summary>
    /// Finds the .nupkg file for a specific package ID and version in the given source directory.
    /// Returns null if not found.
    /// </summary>
    public Task<string?> ResolveAsync(
        string sourceDirectory,
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(
                $"Package source directory not found: '{sourceDirectory}'. " +
                "Ensure the path is accessible from this machine.");

        // Fast path: exact filename match (most common case)
        var exactFile = Path.Combine(sourceDirectory, $"{packageId}.{version}.nupkg");
        if (File.Exists(exactFile))
            return Task.FromResult<string?>(exactFile);

        // Scan all .nupkg files and parse ID + version from each filename.
        // Three resolution tiers — first match in each tier wins:
        //   1. Exact string match          "8.0.0"            → "8.0.0"
        //   2. Semantic equivalence        "2.1"              → "2.1.0"
        //   3. Release-version prefix      "8.0.0"            → "8.0.0-2026040901" (latest if multiple)
        //      Allows operators to specify the release version without caring about build suffixes.
        var allFiles = Directory.GetFiles(sourceDirectory, "*.nupkg", SearchOption.TopDirectoryOnly);

        string?      prefixBestFile    = null;
        NuGetVersion? prefixBestVersion = null;
        NuGetVersion.TryParse(version, out var requestedVer);

        foreach (var file in allFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parsed = ParsePackageFileName(fileName);
            if (parsed is null) continue;

            var (fileId, fileVersion) = parsed.Value;
            if (!fileId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) continue;

            // Tier 1: exact string match
            if (fileVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(file);

            // Tier 2: NuGet semantic equivalence (e.g. "2.1" == "2.1.0")
            if (requestedVer is not null &&
                NuGetVersion.TryParse(fileVersion, out var parsedFileVer) &&
                parsedFileVer == requestedVer)
                return Task.FromResult<string?>(file);

            // Tier 3 candidate: release-version prefix match
            // e.g. requested "8.0.0" matches "8.0.0-2026040901" (has a prerelease label)
            if (requestedVer is not null &&
                NuGetVersion.TryParse(fileVersion, out var pfxVer) &&
                pfxVer.HasMetadata == false &&
                pfxVer.IsPrerelease &&
                pfxVer.Major == requestedVer.Major &&
                pfxVer.Minor == requestedVer.Minor &&
                pfxVer.Patch == requestedVer.Patch &&
                pfxVer.Revision == requestedVer.Revision)
            {
                // Keep the latest prerelease (highest sort order)
                if (prefixBestVersion is null || pfxVer > prefixBestVersion)
                {
                    prefixBestFile    = file;
                    prefixBestVersion = pfxVer;
                }
            }
        }

        return Task.FromResult<string?>(prefixBestFile);
    }

    /// <summary>
    /// Lists all available versions of a package in the source directory.
    /// Useful for 'installer diff' and 'installer status'.
    /// </summary>
    public IReadOnlyList<string> ListAvailableVersions(string sourceDirectory, string packageId)
    {
        if (!Directory.Exists(sourceDirectory))
            return [];

        var versions = new List<NuGetVersion>();

        foreach (var file in Directory.GetFiles(sourceDirectory, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parsed = ParsePackageFileName(fileName);
            if (parsed is null) continue;

            var (fileId, fileVersion) = parsed.Value;
            if (!fileId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) continue;

            if (NuGetVersion.TryParse(fileVersion, out var ver))
                versions.Add(ver);
        }

        versions.Sort();
        return versions.Select(v => v.ToNormalizedString()).ToList().AsReadOnly();
    }

    /// <summary>
    /// Lists all package IDs found in the source directory.
    /// Useful for discovery when setting up new application manifests.
    /// </summary>
    public IReadOnlyList<string> ListAvailablePackages(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return [];

        return Directory
            .GetFiles(sourceDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(f => ParsePackageFileName(Path.GetFileNameWithoutExtension(f)))
            .Where(p => p is not null)
            .Select(p => p!.Value.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Parses a nupkg filename (without extension) into package ID and version.
    ///
    /// Rule: the version begins at the first dot-separated segment that starts with a digit.
    /// This matches the NuGet convention and correctly handles package IDs that contain dots.
    ///
    /// Examples:
    ///   Elements.AddressProviders.Enhetsregisteret.1.18.1  → (Elements.AddressProviders.Enhetsregisteret, 1.18.1)
    ///   DocumentDeliveryService.3.14.0.89-26-gfb4a4b8      → (DocumentDeliveryService, 3.14.0.89-26-gfb4a4b8)
    ///   Elements.AddressProviders.Part3.3.0.0-RC6.1        → (Elements.AddressProviders.Part3, 3.0.0-RC6.1)
    ///   nCoreEis.8.0.0-2026040901                          → (nCoreEis, 8.0.0-2026040901)
    ///   AddressProviders.Folkeregisteret2.1.1.0            → (AddressProviders.Folkeregisteret2, 1.1.0)
    /// </summary>
    public static (string PackageId, string Version)? ParsePackageFileName(string fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            return null;

        var segments = fileNameWithoutExtension.Split('.');

        for (int i = 1; i < segments.Length; i++)
        {
            // Version segment: first segment that begins with a digit
            if (segments[i].Length > 0 && char.IsDigit(segments[i][0]))
            {
                var packageId = string.Join('.', segments[..i]);
                var version   = string.Join('.', segments[i..]);
                return (packageId, version);
            }
        }

        // No version segment found — not a valid package filename
        return null;
    }
}
