using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Setup;

/// <summary>The result of loading a setup file: the normalized plan and its hash when there are no errors, plus every issue found.</summary>
/// <param name="Plan">The normalized plan. Null when the file has an error, so nothing downstream can use a broken plan.</param>
/// <param name="PlanHash">The plan hash. Null exactly when <see cref="Plan"/> is null.</param>
/// <param name="Issues">Every error and hold, in file order where possible. Empty when the file is valid.</param>
public sealed record SetupLoadResult(SetupPlan? Plan, string? PlanHash, IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>The exit code for this result: 0 valid, 1 invalid input, 2 held (doc 45 section 1.7).</summary>
    public SetupExitCode ExitCode => SetupExitCodes.ForIssues(Issues);
}

/// <summary>
/// Reads <c>bazaar-setup.json</c> (doc 45 section 1.5) and returns a normalized plan. The steps are: parse the JSON strictly
/// (no comments, no trailing commas, no duplicate keys), reject unknown keys, check the schema version and the required keys,
/// decode into the typed plan, then run the semantic rules in <see cref="SetupPlanValidator"/>. Pure: no file or network
/// access, and no secret is resolved.
/// </summary>
public static class SetupPlanLoader
{
    /// <summary>The keys each object may hold, by path. Arrays use <c>[]</c>. Paths not listed here are free-form (the settings block).</summary>
    private static readonly Dictionary<string, string[]> _allowedKeys = new(StringComparer.Ordinal)
    {
        [""] =
        [
            "schemaVersion", "install", "database", "custody", "owner", "privacy", "backups", "models", "email",
            "scamMailProtocol", "company", "modules", "network", "firstRun", "settings",
        ],
        ["install"] = ["companyName", "companySlug", "locale", "timezone", "releaseChannel", "updatePolicy"],
        ["install.updatePolicy"] = ["security", "other"],
        ["database"] = ["postgresMajor", "dataRoot", "gcPartitionCount"],
        ["custody"] = ["kekProvider", "kekRef", "auditAnchorTarget", "mergeCredential"],
        ["custody.mergeCredential"] = ["kind", "appIdRef"],
        ["owner"] = ["displayName", "requireSecondFactor", "recovery"],
        ["owner.recovery"] = ["printedCodes", "backupOwners"],
        ["privacy"] = ["profile"],
        ["backups"] = ["target", "drillIntervalDays", "acknowledgeNoTarget"],
        ["models"] = ["sources", "defaultPreset"],
        ["models.sources[]"] = ["id", "keyRef"],
        ["email"] = ["enabled"],
        ["scamMailProtocol"] = ["enabled"],
        ["company"] = ["source"],
        ["network"] = ["statusBind", "statusPort"],
        ["firstRun"] = ["seedGeneralGc", "generalGcTitle", "seedDefaultAgent", "defaultAgentName", "acceptConstitution"],
    };

    /// <summary>Paths whose value may be JSON null (the typed plan's nullable properties). Every other null is an error.</summary>
    internal static readonly HashSet<string> NullablePaths = new(StringComparer.Ordinal)
    {
        "database.dataRoot",
        "custody.auditAnchorTarget",
        "custody.mergeCredential.appIdRef",
        "backups.target",
        "models.sources[].keyRef",
    };

    /// <summary>Keys that must be present. Values are checked by the validator.</summary>
    private static readonly string[] _requiredPaths =
    [
        "schemaVersion", "install", "install.companyName", "install.companySlug", "owner", "owner.displayName",
        "custody", "custody.kekProvider", "custody.kekRef", "company", "company.source",
    ];

    /// <summary>Loads and validates a setup file. <paramref name="registry"/> defaults to the registry this build ships.</summary>
    public static SetupLoadResult Load(string json, SettingRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        registry ??= SettingRegistry.Known;

        var issues = new List<SetupValidationIssue>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            issues.Add(Error("$", $"the file is not valid JSON (line {ex.LineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"})."));
            return new SetupLoadResult(null, null, issues);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Error("$", "the file must hold one JSON object."));
                return new SetupLoadResult(null, null, issues);
            }

            Walk(root, string.Empty, issues);
            CheckSchemaVersion(root, issues);
            CheckRequired(root, issues);
            if (issues.Any(issue => issue.Severity == SetupIssueSeverity.Error))
            {
                return new SetupLoadResult(null, null, issues);
            }

            SetupPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize(root.GetRawText(), SetupPlanJsonContext.Default.SetupPlan);
            }
            catch (JsonException ex)
            {
                // The message names the failing key path. It never quotes the value.
                issues.Add(Error(ex.Path ?? "$", "has a value of the wrong JSON type."));
                return new SetupLoadResult(null, null, issues);
            }

            if (plan is null)
            {
                issues.Add(Error("$", "the file must hold one JSON object."));
                return new SetupLoadResult(null, null, issues);
            }

            SetupPlanValidator.Validate(plan, registry, issues);
            if (issues.Any(issue => issue.Severity == SetupIssueSeverity.Error))
            {
                return new SetupLoadResult(null, null, issues);
            }

            return new SetupLoadResult(plan, SetupPlanCanonicalizer.Hash(plan), issues);
        }
    }

    /// <summary>Walks every object and array. Reports repeated keys anywhere, and unknown keys wherever a schema exists.</summary>
    private static void Walk(JsonElement element, string path, List<SetupValidationIssue> issues)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Walk(item, path + "[]", issues);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _allowedKeys.TryGetValue(path, out var allowed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var childPath = path.Length == 0 ? property.Name : path + "." + property.Name;
            if (!seen.Add(property.Name))
            {
                issues.Add(Error(childPath, "appears more than once; each key may appear once."));
                continue;
            }

            if (allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                issues.Add(Error(childPath, "is not a known key for this file (unknown keys are rejected)."));
                continue;
            }

            // A null is allowed only where the schema says the value may be absent. Elsewhere it would reach the typed plan as null.
            if (allowed is not null && property.Value.ValueKind == JsonValueKind.Null && !NullablePaths.Contains(childPath))
            {
                issues.Add(Error(childPath, "must not be null; omit the key to use its default."));
                continue;
            }

            Walk(property.Value, childPath, issues);
        }
    }

    private static void CheckSchemaVersion(JsonElement root, List<SetupValidationIssue> issues)
    {
        if (!root.TryGetProperty("schemaVersion", out var version))
        {
            return;
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SetupConstants.SchemaVersion)
        {
            issues.Add(Error("schemaVersion", $"is not supported; this build reads schemaVersion {SetupConstants.SchemaVersion}."));
        }
    }

    private static void CheckRequired(JsonElement root, List<SetupValidationIssue> issues)
    {
        foreach (var path in _requiredPaths)
        {
            if (!TryGetPath(root, path))
            {
                issues.Add(Error(path, "is required."));
            }
        }
    }

    private static bool TryGetPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    private static SetupValidationIssue Error(string path, string message) =>
        new(SetupIssueSeverity.Error, path, message);
}
