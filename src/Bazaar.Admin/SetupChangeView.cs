using System.Text.Json.Nodes;
using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// Turns the planner's changes and validation issues into the display model. This is the one place that decides what a value
/// may show. A secret-typed key shows its reference scheme or a count, never the reference text, and a settings key whose name
/// looks like a secret shows no value. The planner itself is unchanged: it holds the canonical values, and this view hides them.
/// </summary>
internal static class SetupChangeView
{
    /// <summary>The reference-typed keys (doc 45 section 1.6). Their canonical text is a reference, which is never printed.</summary>
    private static readonly string[] _referencePaths = ["custody.kekRef", "custody.mergeCredential.appIdRef"];

    /// <summary>The model sources list. It is one leaf in the planner, and each entry holds a reference under keyRef.</summary>
    private const string _sourcesPath = "models.sources";

    private const string _settingsPrefix = "settings.";

    /// <summary>Name fragments that mark a settings key as secret-typed. Matched case-insensitively.</summary>
    private static readonly string[] _secretNameFragments = ["secret", "credential", "password", "token"];

    public static AdminChange ToChange(SetupChange change) => new()
    {
        Path = change.Path,
        Kind = change.Kind switch
        {
            SetupChangeKind.Create => "create",
            SetupChangeKind.Update => "update",
            SetupChangeKind.Remove => "remove",
            _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unknown change kind."),
        },
        Current = Show(change.Path, change.CurrentValue),
        Planned = Show(change.Path, change.PlannedValue),
    };

    public static AdminIssue ToIssue(SetupValidationIssue issue) => new()
    {
        Severity = issue.Severity == SetupIssueSeverity.Hold ? "hold" : "error",
        Path = issue.Path,
        Message = issue.Message,
    };

    /// <summary>The display text of one canonical value, or the placeholder for a secret-typed path. Null stays null.</summary>
    public static string? Show(string path, string? canonical)
    {
        if (canonical is null)
        {
            return null;
        }

        if (Array.IndexOf(_referencePaths, path) >= 0)
        {
            return ReferenceText(canonical);
        }

        if (path == _sourcesPath)
        {
            var count = JsonNode.Parse(canonical) is JsonArray array ? array.Count : 0;
            return $"(list of {count} entries; key references not shown)";
        }

        if (path.StartsWith(_settingsPrefix, StringComparison.Ordinal)
            && IsSecretName(path[_settingsPrefix.Length..]))
        {
            return "(value not shown: secret-typed key)";
        }

        return canonical;
    }

    /// <summary>Shows a reference by its scheme only. A null reference is "not set"; anything that is not a reference is hidden.</summary>
    private static string ReferenceText(string canonical)
    {
        var node = JsonNode.Parse(canonical);
        if (node is null)
        {
            return "not set";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var family = SecretReferences.Classify(text);
            return family == SecretReferenceFamily.None
                ? "(value not shown: not a reference)"
                : $"(reference with scheme {SecretReferences.PrefixOf(family)}, value not shown)";
        }

        return "(value not shown)";
    }

    private static bool IsSecretName(string key) =>
        _secretNameFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
