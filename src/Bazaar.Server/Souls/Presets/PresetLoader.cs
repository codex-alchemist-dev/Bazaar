using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Souls.Presets;

/// <summary>
/// Pure loader and validator for Soul preset sets (docs/design/07-soul-system.md, "Default roster").
/// A set is a JSON array of preset objects. The loader takes a string or a stream and touches no database and no file system.
/// </summary>
/// <remarks>
/// Rejections, all collected in one pass: invalid JSON, a root that is not an array, unknown keys, duplicate keys,
/// duplicate preset ids, personal-data-like keys or values, project or export-excluded content, the global <c>auto</c>
/// family, wildcard or unqualified routes, and routes or whitelist entries outside the preset's whitelist.
/// Routes resolve only as <c>custom/&lt;name&gt;</c> or as a provider-qualified model listed in the whitelist.
/// </remarks>
public static partial class PresetLoader
{
    private const int _maxSpecialties = 16;
    private const int _maxWhitelist = 16;
    private const int _maxDepth = 32;

    private static readonly JsonDocumentOptions _parseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = _maxDepth,
    };

    private static readonly HashSet<string> _presetKeys = new(StringComparer.Ordinal)
    {
        "id", "schemaVersion", "name", "role", "description", "specialties", "modelPolicy", "preferences",
    };

    private static readonly HashSet<string> _modelPolicyKeys = new(StringComparer.Ordinal) { "route", "whitelist" };

    /// <summary>Normalized key fragments that mark personal data. Matched as substrings of the normalized key.</summary>
    private static readonly string[] _personalKeyFragments =
    [
        "email", "phone", "birth", "passport", "password", "passcode", "secret", "apikey", "accesskey", "credential",
        "creditcard", "cardnumber", "iban", "nationalid", "taxid", "firstname", "lastname", "fullname", "surname",
        "givenname", "address", "postcode", "zipcode", "latitude", "longitude", "geolocation", "ipaddress", "medical",
        "diagnosis", "religion", "ethnic", "sexualorientation", "salary",
    ];

    /// <summary>Normalized keys (exact match) that name personal data, project content, or content export excludes (docs/design/07, Export).</summary>
    private static readonly HashSet<string> _personalOrExcludedKeys = new(StringComparer.Ordinal)
    {
        "ssn", "gender", "project", "projects", "projectcontext", "repo", "repos", "repository", "repositories", "branch",
        "workspace", "kanban", "vault", "memory", "memories", "lesson", "lessons", "reputation", "history", "gc", "gcid",
        "gcmessage", "gcmessages", "messages", "passport", "accounts", "embedding", "embeddings", "mirror", "mirrorprofile",
        "vmdisk", "vmdisks", "token", "tokens",
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex SpecialtyPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    /// <summary>Parses and validates a preset set from JSON text.</summary>
    /// <exception cref="PresetValidationException">The set breaks at least one rule. <see cref="PresetValidationException.Issues"/> lists them all.</exception>
    public static IReadOnlyList<Preset> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, _parseOptions);
        }
        catch (JsonException ex)
        {
            throw new PresetValidationException([new PresetIssue(PresetIssueCode.InvalidJson, "$", ex.Message)]);
        }

        using (document)
        {
            return ParseRoot(document.RootElement);
        }
    }

    /// <summary>Parses and validates a preset set from a UTF-8 JSON stream. The stream is read to the end and left open.</summary>
    /// <exception cref="PresetValidationException">The set breaks at least one rule. <see cref="PresetValidationException.Issues"/> lists them all.</exception>
    public static IReadOnlyList<Preset> Parse(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json, _parseOptions);
        }
        catch (JsonException ex)
        {
            throw new PresetValidationException([new PresetIssue(PresetIssueCode.InvalidJson, "$", ex.Message)]);
        }

        using (document)
        {
            return ParseRoot(document.RootElement);
        }
    }

    private static List<Preset> ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new PresetValidationException([new PresetIssue(PresetIssueCode.NotAnArray, "$", "A preset set is a JSON array of preset objects.")]);
        }

        var issues = new List<PresetIssue>();
        var presets = new List<Preset>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in root.EnumerateArray())
        {
            var path = $"$[{index++}]";

            // Duplicate ids are checked on the raw value so that a preset with other problems still counts toward uniqueness.
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("id", out var rawId)
                && rawId.ValueKind == JsonValueKind.String
                && !seenIds.Add(rawId.GetString()!))
            {
                issues.Add(new PresetIssue(PresetIssueCode.DuplicateId, $"{path}.id", $"Preset id '{rawId.GetString()}' is already used in this set."));
            }

            var preset = ParsePreset(element, path, issues);
            if (preset is not null)
            {
                presets.Add(preset);
            }
        }

        if (issues.Count > 0)
        {
            throw new PresetValidationException(issues);
        }

        return presets;
    }

    private static Preset? ParsePreset(JsonElement element, string path, List<PresetIssue> issues)
    {
        var before = issues.Count;
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, path, "A preset is a JSON object."));
            return null;
        }

        ScanValue(element, path, issues);
        CheckUnknownKeys(element, path, _presetKeys, issues);

        var id = RequiredString(element, path, "id", 64, issues);
        if (id is not null && !IdPattern().IsMatch(id))
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidId, $"{path}.id", "Use 1 to 64 lowercase letters, digits or hyphens, starting with a letter or digit."));
        }

        int? schemaVersion = null;
        if (!element.TryGetProperty("schemaVersion", out var versionElement))
        {
            issues.Add(new PresetIssue(PresetIssueCode.MissingKey, path, "'schemaVersion' is required."));
        }
        else if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, $"{path}.schemaVersion", "Must be an integer."));
        }
        else if (version != PresetSchema.Version)
        {
            issues.Add(new PresetIssue(PresetIssueCode.UnsupportedSchemaVersion, $"{path}.schemaVersion", $"This build reads schemaVersion {PresetSchema.Version} only."));
        }
        else
        {
            schemaVersion = version;
        }

        var name = RequiredString(element, path, "name", 64, issues);
        var role = RequiredString(element, path, "role", 128, issues);
        var description = RequiredString(element, path, "description", 1000, issues);
        var specialties = ParseSpecialties(element, path, issues);
        var preferencesJson = ParsePreferences(element, path, issues);
        var modelPolicy = ParseModelPolicyProperty(element, path, issues);

        if (issues.Count != before
            || id is null || schemaVersion is null || name is null || role is null || description is null
            || specialties is null || preferencesJson is null || modelPolicy is null)
        {
            return null;
        }

        return new Preset(id, schemaVersion.Value, name, role, description, specialties, modelPolicy, preferencesJson);
    }

    /// <summary>Walks every value: rejects duplicate keys, personal-data-like keys and email-address strings, at any depth.</summary>
    private static void ScanValue(JsonElement element, string path, List<PresetIssue> issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    if (!seen.Add(property.Name))
                    {
                        issues.Add(new PresetIssue(PresetIssueCode.DuplicateKey, childPath, "The same key appears twice in one object."));
                    }

                    if (IsPersonalOrExcludedKey(property.Name))
                    {
                        issues.Add(new PresetIssue(PresetIssueCode.PersonalDataKey, childPath, "Presets carry behaviour only: no personal data, project context or excluded content."));
                    }

                    ScanValue(property.Value, childPath, issues);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ScanValue(item, $"{path}[{index++}]", issues);
                }

                break;
            case JsonValueKind.String:
                if (EmailPattern().IsMatch(element.GetString()!))
                {
                    issues.Add(new PresetIssue(PresetIssueCode.PersonalDataValue, path, "A string holds an email address."));
                }

                break;
        }
    }

    private static void CheckUnknownKeys(JsonElement element, string path, HashSet<string> allowed, List<PresetIssue> issues)
    {
        foreach (var property in element.EnumerateObject())
        {
            // A personal-data key is reported once, by ScanValue, so it is not reported again here.
            if (!allowed.Contains(property.Name) && !IsPersonalOrExcludedKey(property.Name))
            {
                issues.Add(new PresetIssue(PresetIssueCode.UnknownKey, $"{path}.{property.Name}", "This key is not part of the preset schema."));
            }
        }
    }

    private static string? RequiredString(JsonElement element, string path, string key, int maxLength, List<PresetIssue> issues)
    {
        if (!element.TryGetProperty(key, out var value))
        {
            issues.Add(new PresetIssue(PresetIssueCode.MissingKey, path, $"'{key}' is required."));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, $"{path}.{key}", "Must be a string."));
            return null;
        }

        var text = value.GetString()!;
        if (text.Trim().Length == 0 || text.Length > maxLength)
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, $"{path}.{key}", $"Must be 1 to {maxLength} characters."));
            return null;
        }

        return text;
    }

    private static List<string>? ParseSpecialties(JsonElement element, string path, List<PresetIssue> issues)
    {
        var result = new List<string>();
        if (!element.TryGetProperty("specialties", out var list))
        {
            return result;
        }

        var listPath = $"{path}.specialties";
        if (list.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, listPath, "Must be an array of strings."));
            return null;
        }

        if (list.GetArrayLength() > _maxSpecialties)
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, listPath, $"At most {_maxSpecialties} specialties."));
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            var itemPath = $"{listPath}[{index++}]";
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(new PresetIssue(PresetIssueCode.WrongType, itemPath, "Must be a string."));
                continue;
            }

            var tag = item.GetString()!;
            if (!SpecialtyPattern().IsMatch(tag))
            {
                issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, itemPath, "Use a lowercase slug of up to 48 characters."));
            }
            else if (!seen.Add(tag))
            {
                issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, itemPath, $"Specialty '{tag}' is listed twice."));
            }
            else
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private static string? ParsePreferences(JsonElement element, string path, List<PresetIssue> issues)
    {
        if (!element.TryGetProperty("preferences", out var preferences))
        {
            return "{}";
        }

        var preferencesPath = $"{path}.preferences";
        if (preferences.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, preferencesPath, "Preferences must be a JSON object."));
            return null;
        }

        try
        {
            return SoulCanonicalJson.Canonicalize(preferences.GetRawText());
        }
        catch (ArgumentException ex)
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, preferencesPath, ex.Message));
            return null;
        }
    }

    private static PresetModelPolicy? ParseModelPolicyProperty(JsonElement element, string path, List<PresetIssue> issues)
    {
        if (!element.TryGetProperty("modelPolicy", out var policy))
        {
            issues.Add(new PresetIssue(PresetIssueCode.MissingKey, path, "'modelPolicy' is required."));
            return null;
        }

        if (policy.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, $"{path}.modelPolicy", "Must be an object."));
            return null;
        }

        return ParseModelPolicy(policy, $"{path}.modelPolicy", issues);
    }

    private static PresetModelPolicy? ParseModelPolicy(JsonElement policy, string path, List<PresetIssue> issues)
    {
        var before = issues.Count;
        CheckUnknownKeys(policy, path, _modelPolicyKeys, issues);

        // Whitelist first: a route must be found in it.
        var whitelistPath = $"{path}.whitelist";
        var whitelistText = new List<string>();
        var whitelistModels = new HashSet<ModelRef>();
        if (!policy.TryGetProperty("whitelist", out var whitelist))
        {
            issues.Add(new PresetIssue(PresetIssueCode.MissingKey, path, "'whitelist' is required."));
        }
        else if (whitelist.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, whitelistPath, "Must be an array of provider-qualified models."));
        }
        else if (whitelist.GetArrayLength() == 0)
        {
            issues.Add(new PresetIssue(PresetIssueCode.EmptyWhitelist, whitelistPath, "A route needs a whitelist of at least one model."));
        }
        else if (whitelist.GetArrayLength() > _maxWhitelist)
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, whitelistPath, $"At most {_maxWhitelist} whitelist entries."));
        }
        else
        {
            var index = 0;
            foreach (var item in whitelist.EnumerateArray())
            {
                var itemPath = $"{whitelistPath}[{index++}]";
                if (item.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new PresetIssue(PresetIssueCode.WrongType, itemPath, "Must be a string."));
                    continue;
                }

                var entry = item.GetString()!;
                if (IsAutoName(entry))
                {
                    issues.Add(new PresetIssue(PresetIssueCode.AutoRoute, itemPath, "The global auto family is not allowed in a whitelist."));
                }
                else if (!ModelRef.TryParse(entry, out var model))
                {
                    issues.Add(new PresetIssue(PresetIssueCode.InvalidWhitelistEntry, itemPath, "Use a provider-qualified model, provider:model, with no wildcard."));
                }
                else if (!whitelistModels.Add(model))
                {
                    issues.Add(new PresetIssue(PresetIssueCode.InvalidValue, itemPath, $"Whitelist entry '{entry}' is listed twice."));
                }
                else
                {
                    whitelistText.Add(entry);
                }
            }
        }

        var routePath = $"{path}.route";
        string? route = null;
        if (!policy.TryGetProperty("route", out var routeElement))
        {
            issues.Add(new PresetIssue(PresetIssueCode.MissingKey, path, "'route' is required."));
        }
        else if (routeElement.ValueKind != JsonValueKind.String)
        {
            issues.Add(new PresetIssue(PresetIssueCode.WrongType, routePath, "Must be a string."));
        }
        else
        {
            route = routeElement.GetString()!;
            ValidateRoute(route, routePath, whitelistModels, issues);
        }

        if (issues.Count != before || route is null || whitelistText.Count == 0)
        {
            return null;
        }

        return new PresetModelPolicy(route, whitelistText);
    }

    private static void ValidateRoute(string route, string path, HashSet<ModelRef> whitelist, List<PresetIssue> issues)
    {
        if (IsAutoName(route))
        {
            issues.Add(new PresetIssue(PresetIssueCode.AutoRoute, path, "No global auto routing: use custom/<name> over a whitelist."));
        }
        else if (RouteNames.IsDelegated(route))
        {
            // custom/* and custom/ are wildcards or empty names; RouteNames.IsValid rejects both.
            if (route.Length <= RouteNames.DelegatedPrefix.Length || !RouteNames.IsValid(route))
            {
                issues.Add(new PresetIssue(PresetIssueCode.InvalidRoute, path, "A delegated route is custom/<name>, with a plain name and no wildcard."));
            }
        }
        else if (ModelRef.TryParse(route, out var model))
        {
            if (!whitelist.Contains(model))
            {
                issues.Add(new PresetIssue(PresetIssueCode.RouteNotWhitelisted, path, $"Route '{route}' is not in this preset's whitelist."));
            }
        }
        else
        {
            issues.Add(new PresetIssue(PresetIssueCode.InvalidRoute, path, "Use custom/<name> or a provider-qualified model listed in the whitelist."));
        }
    }

    /// <summary>True for <c>auto</c>, <c>auto/...</c>, and <c>provider:auto/...</c>.</summary>
    private static bool IsAutoName(string text)
    {
        if (RouteNames.IsAuto(text))
        {
            return true;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && RouteNames.IsAuto(text[(colon + 1)..]);
    }

    /// <summary>True for keys that name personal data, project content, or content export excludes.</summary>
    private static bool IsPersonalOrExcludedKey(string key)
    {
        var normalized = Normalize(key);
        if (_personalOrExcludedKeys.Contains(normalized))
        {
            return true;
        }

        foreach (var fragment in _personalKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lowercases and drops every non-letter and non-digit, so <c>first_name</c>, <c>First-Name</c> and <c>firstName</c> compare equal.</summary>
    private static string Normalize(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
