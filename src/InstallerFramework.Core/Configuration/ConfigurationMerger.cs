using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InstallerFramework.Core.Configuration;

/// <summary>
/// Applies deployment parameters to application configuration files.
///
/// Template mode:
///   Replaces {{TokenName}} placeholders in a template string with values from parameters.
///   Produces the final appsettings.json (or any other config file).
///
/// Overlay mode:
///   Generates a minimal JSON object containing only the supplied key-value pairs.
///   Intended as appsettings.{Suffix}.json for ASP.NET Core layered configuration.
///   Keys may use colon notation for nested JSON: "Logging:LogLevel:Default" → nested object.
///
/// Neither mode ever touches passwords or secrets — it's purely structural config.
/// </summary>
public sealed class ConfigurationMerger
{
    private static readonly Regex TokenPattern = new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Template mode: replace {{Key}} tokens in the template with values from parameters.
    /// Throws if a token is present in the template but has no corresponding parameter value.
    /// </summary>
    public string ApplyTemplate(string templateContent, IReadOnlyDictionary<string, string> parameters)
    {
        var unresolvedTokens = new List<string>();

        var result = TokenPattern.Replace(templateContent, match =>
        {
            var key = match.Groups[1].Value.Trim();
            if (parameters.TryGetValue(key, out var value))
                return value;

            unresolvedTokens.Add(key);
            return match.Value; // Leave unresolved tokens in place (allows partial templates)
        });

        if (unresolvedTokens.Count > 0)
            throw new InvalidOperationException(
                $"Template contains unresolved tokens: {string.Join(", ", unresolvedTokens.Select(t => $"{{{{{t}}}}}"))}. " +
                "Add these parameters to the environment manifest.");

        return result;
    }

    /// <summary>
    /// Overlay mode: builds a JSON object from a flat parameter dictionary.
    /// Supports colon-separated keys for nesting: "Section:Key" → { "Section": { "Key": "value" } }
    /// The result is written as appsettings.{Suffix}.json.
    /// </summary>
    public string BuildOverlay(IReadOnlyDictionary<string, string> parameters)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in parameters)
        {
            var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
            SetNestedValue(root, parts, value);
        }

        return System.Text.Json.JsonSerializer.Serialize(root, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    /// <summary>
    /// Merge mode: parses the base appsettings.json from the package, overlays the
    /// supplied parameters on top using colon-notation paths, and returns the result.
    /// Keys not present in parameters keep their original values from the package.
    /// This is the preferred mode for Windows Services — no template file needed,
    /// no values are lost, and you only specify what actually differs per environment.
    /// </summary>
    public string MergeIntoBase(string baseJsonContent, IReadOnlyDictionary<string, string> parameters)
    {
        var root = JsonNode.Parse(baseJsonContent) as JsonObject ?? new JsonObject();

        foreach (var (key, value) in parameters)
        {
            var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            SetJsonNodeValue(root, parts, value);
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static void SetJsonNodeValue(JsonObject node, string[] parts, string value)
    {
        // Keys are matched case-insensitively so that parameters keyed as "AppSettings:Foo"
        // correctly update an existing "Appsettings" section in the base JSON (and vice-versa).
        // This mirrors ASP.NET Core's own configuration key normalisation.
        // When a match IS found the original casing from the base file is preserved;
        // when no match exists the parameter's own casing is used (new key).
        //
        // Some packages store the same logical key twice — once as a literal colon-in-name
        // ("Foo:Bar:Baz": value) and once as a nested object ({ "Foo": { "Bar": { "Baz": value } } }).
        // ASP.NET Core treats both forms as identical, but a JSON file containing both produces
        // a duplicate after merge.  At each level of descent we therefore remove any flat-colon
        // key whose name equals the entire remaining sub-path, so only the nested form survives.
        for (int i = 0; i < parts.Length - 1; i++)
        {
            // Remove any flat-colon sibling key that represents the same remaining path.
            // E.g. when inside AppSettings (i=1) and remaining path is "UnitTest:EphortePrincipal:DefaultDatabase",
            // remove the literal key "UnitTest:EphortePrincipal:DefaultDatabase" from this node.
            var remainingPath = string.Join(":", parts[i..]);
            var flatColonKey = node
                .Select(kvp => kvp.Key)
                .FirstOrDefault(k => k.Equals(remainingPath, StringComparison.OrdinalIgnoreCase));
            if (flatColonKey is not null)
                node.Remove(flatColonKey);

            var resolvedKey = node
                .Select(kvp => kvp.Key)
                .FirstOrDefault(k => k.Equals(parts[i], StringComparison.OrdinalIgnoreCase))
                ?? parts[i];

            if (node[resolvedKey] is not JsonObject child)
            {
                child = new JsonObject();
                node[resolvedKey] = child;
            }
            node = child;
        }

        var resolvedLeaf = node
            .Select(kvp => kvp.Key)
            .FirstOrDefault(k => k.Equals(parts[^1], StringComparison.OrdinalIgnoreCase))
            ?? parts[^1];

        node[resolvedLeaf] = JsonValue.Create(value);
    }

    private static void SetNestedValue(Dictionary<string, object> node, string[] parts, string value)
    {
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!node.TryGetValue(parts[i], out var child) || child is not Dictionary<string, object> childDict)
            {
                childDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                node[parts[i]] = childDict;
            }
            node = (Dictionary<string, object>)node[parts[i]];
        }
        node[parts[^1]] = value;
    }
}
