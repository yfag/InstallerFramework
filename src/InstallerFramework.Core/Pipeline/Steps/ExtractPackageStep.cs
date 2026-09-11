using InstallerFramework.Core.Models;
using InstallerFramework.Core.Packages;
using Microsoft.Extensions.Logging;

namespace InstallerFramework.Core.Pipeline.Steps;

/// <summary>
/// Phase 3 — Install.
/// Copies the .nupkg to a staging directory on the target server and extracts it there.
/// .nupkg is a ZIP archive — we extract the configured content path.
/// On rollback, deletes the staging directory.
/// </summary>
public sealed class ExtractPackageStep : IDeploymentStep
{
    private readonly NuGetPackageResolver _resolver;

    public ExtractPackageStep(NuGetPackageResolver resolver) => _resolver = resolver;

    public string Name => "Extract Package";

    public async Task<StepResult> ExecuteAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.ResolvedPackagePath is null)
            return StepResult.Fail("Package path was not resolved. ResolvePackageStep must run first.");

        var stagingDir = Path.Combine(
            @"C:\Windows\Temp\InstallerFramework",
            context.ApplicationName,
            context.TargetVersion);

        try
        {
            // Ensure staging dir is clean
            await context.Remote.ExecuteScriptAsync(
                $"Remove-Item -Path '{EscapePs(stagingDir)}' -Recurse -Force -ErrorAction SilentlyContinue; " +
                $"New-Item -ItemType Directory -Path '{EscapePs(stagingDir)}' -Force | Out-Null",
                cancellationToken: cancellationToken);

            // Copy the .nupkg to the staging dir via admin share
            var remoteNupkgPath = Path.Combine(stagingDir, Path.GetFileName(context.ResolvedPackagePath));
            await context.Remote.CopyFileToRemoteAsync(
                context.ResolvedPackagePath,
                remoteNupkgPath,
                cancellationToken);

            context.Logger.LogInformation(
                "[{App}@{Server}] Package copied to staging: {Path}",
                context.ApplicationName, context.TargetServer, remoteNupkgPath);

            // Extract on the remote server — .nupkg is a ZIP.
            // Phase 1: extract configured content path (normally "tools/") to {staging}\app\.
            // Phase 2: if a *.zip was found inside tools/, treat it as the inner application
            //          archive (Windows Service or WebDeploy bundle). Locate the app root by
            //          finding the shallowest entry that contains appsettings.json or web.config,
            //          strip that path prefix, and extract the rest to {staging}\content\.
            //          The STAGING: marker in the output tells C# which sub-directory to use.
            var contentPath = context.ApplicationManifest.Application.Package.ContentPath;
            var extractScript = $@"
                $nupkg       = '{EscapePs(remoteNupkgPath)}'
                $staging     = '{EscapePs(stagingDir)}'
                $contentPath = '{EscapePs(contentPath)}'

                Add-Type -AssemblyName System.IO.Compression.FileSystem

                # --- Phase 1: extract nupkg tools/ to app/ ---
                $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
                $extracted = 0
                foreach ($entry in $zip.Entries) {{
                    if ($entry.FullName -notlike ""$contentPath/*"") {{ continue }}
                    if ($entry.Name -eq '') {{ continue }}
                    $relative = $entry.FullName.Substring($contentPath.Length).TrimStart('/')
                    $destFile = Join-Path (Join-Path $staging 'app') $relative
                    $destDir  = Split-Path $destFile
                    if (-not (Test-Path $destDir)) {{
                        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                    }}
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destFile, $true)
                    $extracted++
                }}
                $zip.Dispose()

                if ($extracted -eq 0) {{
                    throw ""No files found under content path '$contentPath' in the package. Check 'package.content-path' in the application manifest.""
                }}
                Write-Output ""Extracted $extracted file(s) from package.""

                # --- Phase 2: extract inner ZIP if present ---
                $appDir   = Join-Path $staging 'app'
                $innerZips = @(Get-ChildItem -Path $appDir -Filter '*.zip' -File -ErrorAction SilentlyContinue)

                if ($innerZips.Count -gt 0) {{
                    $innerZipPath = $innerZips[0].FullName
                    $contentDir   = Join-Path $staging 'content'
                    New-Item -ItemType Directory -Path $contentDir -Force | Out-Null

                    $innerZip = [System.IO.Compression.ZipFile]::OpenRead($innerZipPath)
                    try {{
                        # Find the app root: shallowest entry whose filename is appsettings.json
                        # or web.config. Strip everything above that directory.
                        $markerEntry = $innerZip.Entries |
                            Where-Object {{ $_.Name -ieq 'appsettings.json' -or $_.Name -ieq 'web.config' }} |
                            Sort-Object {{ ($_.FullName -replace '\\','/').Split('/').Count }} |
                            Select-Object -First 1

                        $stripPrefix = ''
                        if ($null -ne $markerEntry) {{
                            $normalized = $markerEntry.FullName -replace '\\','/'
                            $lastSlash  = $normalized.LastIndexOf('/')
                            if ($lastSlash -gt 0) {{
                                $stripPrefix = $normalized.Substring(0, $lastSlash + 1)
                            }}
                        }}

                        $innerCount = 0
                        foreach ($entry in $innerZip.Entries) {{
                            if ($entry.Name -eq '') {{ continue }}
                            $entryPath = $entry.FullName -replace '\\','/'
                            $relative  = if ($stripPrefix -ne '' -and
                                            $entryPath.ToLower().StartsWith($stripPrefix.ToLower())) {{
                                $entryPath.Substring($stripPrefix.Length)
                            }} else {{
                                $entryPath
                            }}
                            $relative = $relative.TrimStart('/')
                            if ($relative -eq '') {{ continue }}
                            $destFile = Join-Path $contentDir $relative
                            $destDir  = Split-Path $destFile
                            if (-not (Test-Path $destDir)) {{
                                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                            }}
                            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destFile, $true)
                            $innerCount++
                        }}
                        Write-Output ""Inner ZIP extracted: $innerCount file(s) to content/.""
                        Write-Output ""STAGING:content""
                    }} finally {{
                        $innerZip.Dispose()
                    }}
                }} else {{
                    Write-Output ""STAGING:app""
                }}
            ";

            var result = await context.Remote.ExecuteScriptAsync(extractScript, cancellationToken: cancellationToken);
            if (!result.Success)
                return StepResult.Fail($"Package extraction failed: {result.Errors}");

            // The script's last STAGING: line tells us which sub-directory holds the app files.
            var stagingLine = result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => l.TrimStart().StartsWith("STAGING:", StringComparison.OrdinalIgnoreCase));
            var stagingSubDir = stagingLine is not null
                ? stagingLine.TrimStart()[8..].Trim()
                : "app";

            context.RemoteStagingDirectory = Path.Combine(stagingDir, stagingSubDir);

            context.Logger.LogInformation(
                "[{App}@{Server}] {Output}",
                context.ApplicationName, context.TargetServer,
                // Log everything except the internal STAGING: marker line
                string.Join('\n', result.Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => !l.TrimStart().StartsWith("STAGING:", StringComparison.OrdinalIgnoreCase)))
                .Trim());

            return StepResult.Ok();
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Package extraction error: {ex.Message}");
        }
    }

    public async Task RollbackAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (context.RemoteStagingDirectory is null) return;
        try
        {
            var stagingParent = Path.GetDirectoryName(context.RemoteStagingDirectory)!;
            await context.Remote.DeleteDirectoryAsync(stagingParent, recursive: true, cancellationToken);
            context.Logger.LogInformation("[{App}@{Server}] Rollback: staging directory removed.", context.ApplicationName, context.TargetServer);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "[{App}@{Server}] Rollback: could not remove staging directory.", context.ApplicationName, context.TargetServer);
        }
    }

    private static string EscapePs(string path) => path.Replace("'", "''");
}
