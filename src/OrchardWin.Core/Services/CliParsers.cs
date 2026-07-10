using System.Text.Json;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// Pure parsing of CLI / HTTP output. These functions depend only on their inputs and the
/// app's domain models, so they unit-test directly. Lenient by design: malformed input
/// yields an empty/no-op result rather than throwing - ported from Orchard's `CLIParsers.swift`.
public static class CliParsers
{
    // MARK: - Builder status

    public enum BuilderParseKind { NotRunning, Builders, DecodeFailure }

    public sealed record BuilderParseResult(BuilderParseKind Kind, IReadOnlyList<Builder> Builders, string? Preview);

    /// Outcome of parsing `wslc builder status --format json` stdout (subcommand name is a
    /// best-effort mirror of Orchard's `container builder status` - verify against `wslc
    /// build --help` once on Windows; see ARCHITECTURE.md).
    public static BuilderParseResult ParseBuilderStatus(string stdout)
    {
        var trimmed = stdout.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower.StartsWith("builder is not running", StringComparison.Ordinal) || lower.StartsWith("no builder", StringComparison.Ordinal))
            return new BuilderParseResult(BuilderParseKind.NotRunning, [], null);

        if (trimmed.Length == 0 || trimmed == "null" || trimmed == "[]")
            return new BuilderParseResult(BuilderParseKind.NotRunning, [], null);

        try
        {
            if (trimmed.StartsWith('['))
            {
                var array = JsonSerializer.Deserialize<List<Builder>>(trimmed);
                if (array is not null) return new BuilderParseResult(BuilderParseKind.Builders, array, null);
            }
            else
            {
                var single = JsonSerializer.Deserialize<Builder>(trimmed);
                if (single is not null) return new BuilderParseResult(BuilderParseKind.Builders, [single], null);
            }
        }
        catch (JsonException)
        {
            // fall through to decode failure
        }

        return new BuilderParseResult(BuilderParseKind.DecodeFailure, [], trimmed.Length > 200 ? trimmed[..200] : trimmed);
    }

    // MARK: - System properties

    /// Legacy id aliases, mapping category keys to the ids the app looks up.
    private static readonly Dictionary<string, string> SystemPropertyIdAliases = new()
    {
        ["build.image"] = "image.builder",
        ["vminit.image"] = "image.init",
    };

    /// Parses `wslc system property list --format=json`. Handles both a nested
    /// `{"category": {"key": value}}` object and a flat `[{id,type,value,description}]`
    /// array, since which shape wslc actually emits wasn't verifiable from macOS.
    public static List<SystemProperty> ParseSystemProperties(string output)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(output); }
        catch (JsonException) { return []; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var result = new List<SystemProperty>();
                foreach (var category in root.EnumerateObject())
                {
                    if (category.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var field in category.Value.EnumerateObject())
                    {
                        var rawId = $"{category.Name}.{field.Name}";
                        var (type, value) = NormalizeSystemPropertyValue(field.Value);
                        result.Add(new SystemProperty
                        {
                            Id = SystemPropertyIdAliases.GetValueOrDefault(rawId, rawId),
                            Type = type,
                            Value = value,
                            Description = "",
                        });
                    }
                }
                return result;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var result = new List<SystemProperty>();
                foreach (var entry in root.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String) continue;
                    var rawId = idProp.GetString()!;
                    var type = entry.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "Bool"
                        ? PropertyType.Bool : PropertyType.String;

                    string valueString;
                    if (!entry.TryGetProperty("value", out var valueProp) || valueProp.ValueKind == JsonValueKind.Null)
                        valueString = "*undefined*";
                    else if (type == PropertyType.Bool && valueProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        valueString = valueProp.GetBoolean() ? "true" : "false";
                    else if (valueProp.ValueKind == JsonValueKind.String)
                        valueString = valueProp.GetString()!;
                    else
                        valueString = valueProp.GetRawText();

                    var description = entry.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String
                        ? descProp.GetString()! : "";

                    result.Add(new SystemProperty
                    {
                        Id = SystemPropertyIdAliases.GetValueOrDefault(rawId, rawId),
                        Type = type,
                        Value = valueString,
                        Description = description,
                    });
                }
                return result;
            }

            return [];
        }
    }

    private static (PropertyType, string) NormalizeSystemPropertyValue(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Null => (PropertyType.String, "*undefined*"),
        JsonValueKind.String => (PropertyType.String, raw.GetString()!),
        JsonValueKind.True or JsonValueKind.False => (PropertyType.Bool, raw.GetBoolean() ? "true" : "false"),
        JsonValueKind.Number => (PropertyType.String, raw.GetRawText()),
        _ => (PropertyType.String, raw.GetRawText()),
    };

    // MARK: - Docker Hub search

    public static List<RegistrySearchResult> ParseDockerHubSearch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<RegistrySearchResult>();
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("repo_name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
                var name = nameProp.GetString()!;
                var fullName = name.Contains('/') ? $"docker.io/{name}" : $"docker.io/library/{name}";

                list.Add(new RegistrySearchResult
                {
                    Name = fullName,
                    Description = result.TryGetProperty("short_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    IsOfficial = result.TryGetProperty("is_official", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False && o.GetBoolean(),
                    StarCount = result.TryGetProperty("star_count", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null,
                });
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
