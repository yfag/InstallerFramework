using InstallerFramework.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace InstallerFramework.Core.Configuration;

/// <summary>
/// Loads and deserializes YAML manifest files.
/// Both manifest types use kebab-case YAML keys.
/// </summary>
public sealed class ManifestLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithTypeConverter(new ApplicationTypeConverter())
        .WithTypeConverter(new DotNetRuntimeConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    // -------------------------------------------------------------------------
    // Enum converters
    //
    // YamlDotNet 16.x does not apply the naming convention to enum VALUES during
    // deserialization — only to property KEYS. So "iis-application" in YAML will
    // never match ApplicationType.IisApplication via Enum.Parse. Custom converters
    // bypass this entirely and own the mapping directly.
    // -------------------------------------------------------------------------

    private sealed class ApplicationTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ApplicationType);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var value = parser.Consume<Scalar>().Value;
            // Accept both kebab-case (YAML style) and PascalCase (C# name)
            return value.Replace("-", "").ToLowerInvariant() switch
            {
                "iisapplication"  => ApplicationType.IisApplication,
                "windowsservice"  => ApplicationType.WindowsService,
                _ => throw new YamlException(
                    $"Unknown application type '{value}'. Expected 'iis-application' or 'windows-service'.")
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer rootSerializer) =>
            emitter.Emit(new Scalar((ApplicationType)value! switch
            {
                ApplicationType.IisApplication => "iis-application",
                ApplicationType.WindowsService => "windows-service",
                _ => value.ToString()!
            }));
    }

    private sealed class DotNetRuntimeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DotNetRuntime);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var value = parser.Consume<Scalar>().Value;
            return value.ToLowerInvariant() switch
            {
                "core"      => DotNetRuntime.Core,
                "framework" => DotNetRuntime.Framework,
                _ => throw new YamlException(
                    $"Unknown .NET runtime '{value}'. Expected 'Core' or 'Framework'.")
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer rootSerializer) =>
            emitter.Emit(new Scalar((DotNetRuntime)value! switch
            {
                DotNetRuntime.Core      => "Core",
                DotNetRuntime.Framework => "Framework",
                _ => value.ToString()!
            }));
    }

    /// <summary>Loads an application manifest from a .yaml file path.</summary>
    public ApplicationManifest LoadApplicationManifest(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Application manifest not found: {filePath}");

        var yaml = File.ReadAllText(filePath);
        return ParseApplicationManifest(yaml, filePath);
    }

    /// <summary>Loads an environment manifest from a .yaml file path.</summary>
    public EnvironmentManifest LoadEnvironmentManifest(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Environment manifest not found: {filePath}");

        var yaml = File.ReadAllText(filePath);
        return ParseEnvironmentManifest(yaml, filePath);
    }

    /// <summary>
    /// Loads all application manifests referenced by an environment manifest.
    /// Resolves .application.yaml files from the manifests-path directory.
    /// </summary>
    public Dictionary<string, ApplicationManifest> LoadApplicationManifests(
        EnvironmentManifest environmentManifest,
        string environmentManifestFilePath)
    {
        var envDir = Path.GetDirectoryName(Path.GetFullPath(environmentManifestFilePath))!;
        var manifestsPath = Path.IsPathRooted(environmentManifest.Environment.ManifestsPath)
            ? environmentManifest.Environment.ManifestsPath
            : Path.Combine(envDir, environmentManifest.Environment.ManifestsPath);

        var result = new Dictionary<string, ApplicationManifest>(StringComparer.OrdinalIgnoreCase);

        foreach (var deployment in environmentManifest.Environment.Deployments)
        {
            if (result.ContainsKey(deployment.Application)) continue;

            // Convention: {ApplicationName}.application.yaml
            var manifestFile = Path.Combine(manifestsPath, $"{deployment.Application}.application.yaml");
            if (!File.Exists(manifestFile))
                throw new FileNotFoundException(
                    $"Application manifest for '{deployment.Application}' not found at: {manifestFile}");

            result[deployment.Application] = LoadApplicationManifest(manifestFile);
        }

        return result;
    }

    private static ApplicationManifest ParseApplicationManifest(string yaml, string source)
    {
        try
        {
            return Deserializer.Deserialize<ApplicationManifest>(yaml)
                   ?? throw new InvalidDataException($"Empty or null manifest in {source}");
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidDataException($"Failed to parse application manifest '{source}': {ex.Message}", ex);
        }
    }

    private static EnvironmentManifest ParseEnvironmentManifest(string yaml, string source)
    {
        try
        {
            return Deserializer.Deserialize<EnvironmentManifest>(yaml)
                   ?? throw new InvalidDataException($"Empty or null manifest in {source}");
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidDataException($"Failed to parse environment manifest '{source}': {ex.Message}", ex);
        }
    }
}
