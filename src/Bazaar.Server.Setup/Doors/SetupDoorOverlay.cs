using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Setup;

/// <summary>
/// Merges the three setup doors into one plan document (doc 45 section 1.2, "one plan, many doors"). The merged text is then
/// loaded and validated by <see cref="SetupPlanLoader"/> like any file.
/// </summary>
/// <remarks>
/// Precedence, lowest to highest: the file, then the <c>BAZAAR__SETUP__</c> environment variables, then <c>--set path=value</c>
/// flags. A later door wins for the same key. Environment variables are applied in name order, and two names that resolve to
/// the same key are an error, because the environment has no order of its own to break the tie.
///
/// Environment names: the text after the prefix is split at each double underscore into path segments. Segments match
/// case-insensitively and are written in the plan's camelCase form, so <c>BAZAAR__SETUP__INSTALL__COMPANYNAME</c> sets
/// <c>install.companyName</c>. A list item takes a numeric segment, such as <c>MODELS__SOURCES__0__ID</c>. The settings block takes
/// registry keys, which contain dots, so everything after <c>settings</c> is one key. Flags use dots instead of double underscores.
///
/// Values are typed by the plan's shape: a string as text, an int as a whole number, a bool as true or false, and a list as a JSON
/// array of its items. A value for <c>null</c> on a nullable key (for example <c>backups.target</c>) is the text <c>null</c>. An empty
/// environment variable is unset and is ignored. A secret-typed key accepts only a reference (doc 45 section 1.6); a literal fails
/// with the key name, never the value.
///
/// Every error names the key path and the rule, and never a value. The merge reads only the strings and the dictionary it is given,
/// never the process environment (the caller passes that in through <see cref="ISetupEnvironment"/>). A base document that is not
/// valid JSON is returned unchanged, so the loader reports the syntax error.
/// </remarks>
public static class SetupDoorOverlay
{
    /// <summary>The prefix of the environment variables the overlay reads (doc 45 section 1.1, the convention of doc 03).</summary>
    public const string EnvironmentPrefix = "BAZAAR__SETUP__";

    private const string _segmentSeparator = "__";
    private const string _flagName = "--set";

    /// <summary>True when at least one environment variable with the prefix has a value. The CLI uses it to decide whether a file is needed.</summary>
    public static bool HasEnvironmentOverrides(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Any(entry => IsOverride(entry.Key, entry.Value));
    }

    /// <summary>
    /// Merges the file (or an empty document when <paramref name="baseJson"/> is null), the environment variables and the flags.
    /// </summary>
    /// <param name="baseJson">The file text, or null when no file was given. A null base is the empty document.</param>
    /// <param name="setArguments">The <c>path=value</c> flags, in the order given. A repeated path takes the later value.</param>
    /// <param name="environment">The environment variables. Only names with the prefix are read.</param>
    /// <param name="settings">The settings catalog the <c>settings.*</c> keys are resolved against.</param>
    public static SetupOverlayResult Merge(string? baseJson, IReadOnlyList<string> setArguments, IReadOnlyDictionary<string, string?> environment, ISetupSettingCatalog settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(setArguments);
        ArgumentNullException.ThrowIfNull(environment);

        var doors = SetupDoors.None;
        var issues = new List<SetupValidationIssue>();
        JsonObject root;
        if (baseJson is null)
        {
            root = new JsonObject();
        }
        else
        {
            doors |= SetupDoors.File;
            if (!TryParseObject(baseJson, out var parsed))
            {
                // Not one JSON object: the loader gives the exact syntax or shape error for the file.
                return new SetupOverlayResult(baseJson, doors, issues);
            }

            root = parsed;
        }

        var environmentPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in OverridesIn(environment))
        {
            doors |= SetupDoors.EnvironmentVariables;
            var segments = name.Split(_segmentSeparator, StringSplitOptions.None);
            ApplyOne(root, segments, value, issues, environmentPaths, settings);
        }

        foreach (var argument in setArguments)
        {
            doors |= SetupDoors.Flags;
            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                // The text is not echoed: only the flag name is named.
                issues.Add(new SetupValidationIssue(
                    SetupIssueSeverity.Error,
                    _flagName,
                    "must be a path and a value joined by an equals sign, for example install.companyName=Example Studio."));
                continue;
            }

            ApplyOne(root, argument[..equals].Split('.'), argument[(equals + 1)..], issues, seen: null, settings);
        }

        if (issues.Count > 0)
        {
            return new SetupOverlayResult(null, doors, issues);
        }

        return new SetupOverlayResult(root.ToJsonString(), doors, issues);
    }

    /// <summary>The environment variables that override the plan, in name order. Empty values are unset and are left out.</summary>
    private static IEnumerable<(string Name, string Value)> OverridesIn(IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var entry in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (IsOverride(entry.Key, entry.Value))
            {
                yield return (entry.Key[EnvironmentPrefix.Length..], entry.Value!);
            }
        }
    }

    private static bool IsOverride(string name, string? value) =>
        name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value);

    /// <summary>Resolves one key and writes its value. <paramref name="seen"/> catches two environment names for one key.</summary>
    private static void ApplyOne(JsonObject root, IReadOnlyList<string> segments, string text, List<SetupValidationIssue> issues, HashSet<string>? seen, ISetupSettingCatalog settings)
    {
        var (target, issue) = SetupKeyResolver.Resolve(segments, settings);
        if (issue is not null || target is null)
        {
            issues.Add(issue ?? new SetupValidationIssue(SetupIssueSeverity.Error, string.Join('.', segments), "is not a known setup key."));
            return;
        }

        if (seen is not null && !seen.Add(target.Dotted))
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, target.Dotted, "is set more than once in the environment; set it under one name."));
            return;
        }

        var (node, valueIssue) = SetupKeyResolver.ToNode(target, text);
        if (valueIssue is not null)
        {
            issues.Add(valueIssue);
            return;
        }

        var putIssue = SetupKeyResolver.Put(root, target, node);
        if (putIssue is not null)
        {
            issues.Add(putIssue);
        }
    }

    /// <summary>Parses the file text as one JSON object with the loader's rules (no comments, no trailing commas). False on anything else.</summary>
    private static bool TryParseObject(string json, out JsonObject root)
    {
        root = null!;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (node is not JsonObject parsed)
            {
                return false;
            }

            root = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // A duplicate key throws here; the loader reports it with its key path.
            return false;
        }
    }
}
