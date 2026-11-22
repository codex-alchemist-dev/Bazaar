using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// The structural pass over <c>template.json</c>, before anything is decoded (doc 45 section 1.8). It reports repeated keys at any
/// depth, unknown keys wherever a schema exists, nulls where a value is required, keys in the never-contains deny-list, and secret
/// references in any string. Paths are shown with array indexes (<c>roles[0].id</c>); schema lookups use <c>[]</c>.
/// </summary>
internal static class TemplateDocumentWalker
{
    /// <summary>The keys each object may hold, by schema path. Paths not listed are free-form (settings and preset maps).</summary>
    private static readonly Dictionary<string, string[]> _allowedKeys = new(StringComparer.Ordinal)
    {
        [""] =
        [
            "schemaVersion", "version", "id", "modules", "settings", "roles", "gcTypes", "kanbanColumns",
            "calendarPresets", "quotaPresets", "soulPresets", "seeds",
        ],
        ["roles[]"] = ["id", "displayName", "permissions"],
        ["seeds"] = ["constitutionDraft", "roleCharters", "vaultLayout"],
    };

    /// <summary>Keys that must be present. Values are checked by the validator.</summary>
    private static readonly string[] _requiredPaths = ["schemaVersion", "version", "id", "modules"];

    /// <summary>Walks the whole document, adding one issue per problem found.</summary>
    public static void Walk(JsonElement root, List<SetupValidationIssue> issues)
    {
        WalkElement(root, string.Empty, string.Empty, issues);
        CheckRequired(root, issues);
    }

    private static void WalkElement(JsonElement element, string schemaPath, string displayPath, List<SetupValidationIssue> issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var itemPath = displayPath + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    index++;
                    if (item.ValueKind == JsonValueKind.Null)
                    {
                        issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, itemPath, "must not be null."));
                        continue;
                    }

                    WalkElement(item, schemaPath + "[]", itemPath, issues);
                }

                return;

            case JsonValueKind.String:
                if (TemplateContentRules.HasSecretReference(element.GetString() ?? string.Empty))
                {
                    issues.Add(ValueError(displayPath));
                }

                return;

            case JsonValueKind.Object:
                WalkObject(element, schemaPath, displayPath, issues);
                return;
        }
    }

    private static void WalkObject(JsonElement element, string schemaPath, string displayPath, List<SetupValidationIssue> issues)
    {
        _allowedKeys.TryGetValue(schemaPath, out var allowed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var childDisplay = Join(displayPath, property.Name);
            var childSchema = Join(schemaPath, property.Name);

            if (!seen.Add(property.Name))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, childDisplay, "appears more than once; each key may appear once."));
                continue;
            }

            var category = TemplateContentRules.KeyCategory(property.Name);
            if (category is not null)
            {
                issues.Add(new SetupValidationIssue(
                    SetupIssueSeverity.Error,
                    childDisplay,
                    $"is a key for {category}; a template never contains {category} (doc 45 section 1.8)."));
                continue;
            }

            if (TemplateContentRules.HasSecretReference(property.Name))
            {
                // The key itself may be the secret, so the path stops at its parent: the key text never reaches a message.
                issues.Add(new SetupValidationIssue(
                    SetupIssueSeverity.Error,
                    displayPath.Length == 0 ? "$" : displayPath,
                    $"has a key that holds a secret reference; a template never contains {TemplateContentRules.SecretCategory} (doc 45 section 1.8)."));
                continue;
            }

            if (allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, childDisplay, "is not a known key for a template (unknown keys are rejected)."));
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, childDisplay, "must not be null; omit the key to use its default."));
                continue;
            }

            WalkElement(property.Value, childSchema, childDisplay, issues);
        }
    }

    private static void CheckRequired(JsonElement root, List<SetupValidationIssue> issues)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var path in _requiredPaths)
        {
            if (!root.TryGetProperty(path, out _))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path, "is required."));
            }
        }
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;

    private static SetupValidationIssue ValueError(string path) =>
        new(SetupIssueSeverity.Error, path, $"holds a secret reference; a template never contains {TemplateContentRules.SecretCategory} (doc 45 section 1.8).");
}
