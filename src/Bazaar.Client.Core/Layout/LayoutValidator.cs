using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Client.Core.Layout;

/// <summary>
/// Parses and validates a layout document. Pure: no Avalonia types and no I/O. Never throws on bad input.
/// Rules (doc 41 section 6): data only, schemaVersion 1, known fields only, bounded size, depth, pane and child counts,
/// and no URL, markup, script or code in any string, keys included. Parsing is manual over <see cref="JsonElement"/>,
/// so no reflection-based serializer is involved.
/// </summary>
public static partial class LayoutValidator
{
    private static readonly string[] _forbiddenFragments =
    [
        "//", "www.", "url(", "@import", "javascript:", "vbscript:", "data:", "expression(", "<", ">", "{", "}", "\\",
    ];

    private static readonly HashSet<string> _topLevelFields = new(StringComparer.Ordinal) { "schemaVersion", "name", "locks", "slots" };
    private static readonly HashSet<string> _slotFields = new(StringComparer.Ordinal) { "deviceClass", "display", "posture", "root" };
    private static readonly HashSet<string> _nodeFields = new(StringComparer.Ordinal) { "container", "children", "paneKind", "paneId", "paneConfig" };

    /// <summary>Loads and validates a layout document from JSON text.</summary>
    public static LayoutLoadResult Validate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var issues = new List<LayoutIssue>();
        if (json.Length > LayoutVocabulary.MaxDocumentChars)
        {
            issues.Add(new LayoutIssue("", "too-large", $"Layout is larger than {LayoutVocabulary.MaxDocumentChars} characters."));
            return new LayoutLoadResult(null, issues);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 64,
            });
        }
        catch (JsonException ex)
        {
            issues.Add(new LayoutIssue("", "json-syntax", ex.Message));
            return new LayoutLoadResult(null, issues);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new LayoutIssue("", "not-object", "Layout must be a JSON object."));
                return new LayoutLoadResult(null, issues);
            }

            ScanStrings(root, "", issues);
            CheckUnknownFields(root, "", _topLevelFields, issues);

            int schemaVersion = CheckSchemaVersion(root, issues);
            string? name = CheckName(root, issues);
            List<string> locks = CheckLocks(root, issues);
            List<LayoutSlot> slots = CheckSlots(root, issues);

            if (issues.Count > 0 || name is null)
            {
                return new LayoutLoadResult(null, issues);
            }

            return new LayoutLoadResult(new LayoutDocument(schemaVersion, name, locks, slots), []);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.()-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9_-]*(\\.[a-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PaneKindPattern();

    private static int CheckSchemaVersion(JsonElement root, List<LayoutIssue> issues)
    {
        if (!root.TryGetProperty("schemaVersion", out JsonElement value))
        {
            issues.Add(new LayoutIssue("/schemaVersion", "missing-field", "schemaVersion is required."));
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version) || version != LayoutVocabulary.SchemaVersion)
        {
            issues.Add(new LayoutIssue("/schemaVersion", "unsupported-schema-version", $"schemaVersion must be {LayoutVocabulary.SchemaVersion}."));
            return 0;
        }

        return version;
    }

    private static string? CheckName(JsonElement root, List<LayoutIssue> issues)
    {
        if (!root.TryGetProperty("name", out JsonElement value))
        {
            issues.Add(new LayoutIssue("/name", "missing-field", "name is required."));
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } name || !NamePattern().IsMatch(name))
        {
            issues.Add(new LayoutIssue("/name", "bad-name", "Name must be 1 to 64 characters: a letter or digit, then letters, digits, spaces and _ . ( ) -."));
            return null;
        }

        return name;
    }

    private static List<string> CheckLocks(JsonElement root, List<LayoutIssue> issues)
    {
        var locks = new List<string>();
        if (!root.TryGetProperty("locks", out JsonElement array))
        {
            return locks;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new LayoutIssue("/locks", "wrong-kind", "locks must be an array of slot patterns."));
            return locks;
        }

        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string path = $"/locks/{i++}";
            if (item.ValueKind != JsonValueKind.String || !IsSlotPattern(item.GetString()!))
            {
                issues.Add(new LayoutIssue(path, "bad-lock", "A lock must be '*' or deviceClass/display/posture with known values or '*'."));
                continue;
            }

            locks.Add(item.GetString()!);
        }

        return locks;
    }

    private static List<LayoutSlot> CheckSlots(JsonElement root, List<LayoutIssue> issues)
    {
        var slots = new List<LayoutSlot>();
        if (!root.TryGetProperty("slots", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new LayoutIssue("/slots", "missing-field", "slots is required and must be an array."));
            return slots;
        }

        if (array.GetArrayLength() == 0 || array.GetArrayLength() > LayoutVocabulary.MaxSlots)
        {
            issues.Add(new LayoutIssue("/slots", "bad-count", $"slots must hold 1 to {LayoutVocabulary.MaxSlots} entries."));
            return slots;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string path = $"/slots/{i++}";
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new LayoutIssue(path, "wrong-kind", "A slot must be an object."));
                continue;
            }

            CheckUnknownFields(item, path, _slotFields, issues);
            string? deviceClass = ReadEnum(item, "deviceClass", LayoutVocabulary.DeviceClasses, path, issues);
            string? display = ReadEnum(item, "display", LayoutVocabulary.Displays, path, issues);
            string? posture = ReadEnum(item, "posture", LayoutVocabulary.Postures, path, issues);

            if (!item.TryGetProperty("root", out JsonElement rootNode) || rootNode.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new LayoutIssue(path + "/root", "missing-field", "A slot needs a root node object."));
                continue;
            }

            var panes = new List<string>();
            LayoutNode? node = CheckNode(rootNode, path + "/root", 1, panes, issues);
            if (panes.Count > LayoutVocabulary.MaxPanesPerSlot)
            {
                issues.Add(new LayoutIssue(path, "too-many-panes", $"A slot may place at most {LayoutVocabulary.MaxPanesPerSlot} panes."));
            }

            if (deviceClass is null || display is null || posture is null || node is null)
            {
                continue;
            }

            if (!seen.Add($"{deviceClass}/{display}/{posture}"))
            {
                issues.Add(new LayoutIssue(path, "duplicate-slot", "Two slots share the same deviceClass, display and posture."));
                continue;
            }

            slots.Add(new LayoutSlot(deviceClass, display, posture, node));
        }

        return slots;
    }

    private static LayoutNode? CheckNode(JsonElement element, string path, int depth, List<string> panes, List<LayoutIssue> issues)
    {
        if (depth > LayoutVocabulary.MaxNestingDepth)
        {
            issues.Add(new LayoutIssue(path, "too-deep", $"Nesting is limited to {LayoutVocabulary.MaxNestingDepth} levels."));
            return null;
        }

        CheckUnknownFields(element, path, _nodeFields, issues);
        bool hasContainer = element.TryGetProperty("container", out _);
        bool hasPane = element.TryGetProperty("paneKind", out _);
        if (hasContainer == hasPane)
        {
            issues.Add(new LayoutIssue(path, "bad-node", "A node is either a container or a pane, not both and not neither."));
            return null;
        }

        string? paneId = null;
        if (element.TryGetProperty("paneId", out JsonElement idValue))
        {
            if (idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { Length: > 0 and <= 64 } id)
            {
                issues.Add(new LayoutIssue(path + "/paneId", "bad-pane-id", "paneId must be a string of 1 to 64 characters."));
                return null;
            }

            paneId = id;
        }

        if (hasPane)
        {
            if (!element.TryGetProperty("paneKind", out JsonElement kindValue) || kindValue.ValueKind != JsonValueKind.String
                || !PaneKindPattern().IsMatch(kindValue.GetString() ?? string.Empty) || kindValue.GetString()!.Length > 64)
            {
                issues.Add(new LayoutIssue(path + "/paneKind", "bad-pane-kind", "paneKind must be a dotted lower-case name of up to 64 characters."));
                return null;
            }

            panes.Add(kindValue.GetString()!);
            JsonElement? config = element.TryGetProperty("paneConfig", out JsonElement configValue) ? configValue.Clone() : null;
            return new LayoutNode(null, [], kindValue.GetString(), paneId, config);
        }

        if (!element.TryGetProperty("container", out JsonElement containerValue) || containerValue.ValueKind != JsonValueKind.String
            || Array.IndexOf(LayoutVocabulary.Containers, containerValue.GetString()) < 0)
        {
            issues.Add(new LayoutIssue(path + "/container", "unknown-container", "container must be row, column, tabs or stack."));
            return null;
        }

        if (!element.TryGetProperty("children", out JsonElement children) || children.ValueKind != JsonValueKind.Array
            || children.GetArrayLength() == 0 || children.GetArrayLength() > LayoutVocabulary.MaxChildrenPerContainer)
        {
            issues.Add(new LayoutIssue(path + "/children", "bad-children", $"children must be an array of 1 to {LayoutVocabulary.MaxChildrenPerContainer} nodes."));
            return null;
        }

        var nodes = new List<LayoutNode>();
        int i = 0;
        foreach (JsonElement child in children.EnumerateArray())
        {
            string childPath = $"{path}/children/{i++}";
            if (child.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new LayoutIssue(childPath, "wrong-kind", "A child must be a node object."));
                continue;
            }

            LayoutNode? built = CheckNode(child, childPath, depth + 1, panes, issues);
            if (built is not null)
            {
                nodes.Add(built);
            }
        }

        return new LayoutNode(containerValue.GetString(), nodes, null, paneId, null);
    }

    private static string? ReadEnum(JsonElement item, string field, string[] allowed, string path, List<LayoutIssue> issues)
    {
        if (!item.TryGetProperty(field, out JsonElement value))
        {
            issues.Add(new LayoutIssue($"{path}/{field}", "missing-field", $"{field} is required."));
            return null;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null || (text != LayoutVocabulary.Any && Array.IndexOf(allowed, text) < 0))
        {
            issues.Add(new LayoutIssue($"{path}/{field}", "bad-value", $"{field} must be one of {string.Join(", ", allowed)} or *."));
            return null;
        }

        return text;
    }

    private static bool IsSlotPattern(string pattern)
    {
        if (pattern == LayoutVocabulary.Any)
        {
            return true;
        }

        string[] parts = pattern.Split('/');
        return parts.Length == 3
            && MatchesOrAny(parts[0], LayoutVocabulary.DeviceClasses)
            && MatchesOrAny(parts[1], LayoutVocabulary.Displays)
            && MatchesOrAny(parts[2], LayoutVocabulary.Postures);
    }

    private static bool MatchesOrAny(string part, string[] allowed) =>
        part == LayoutVocabulary.Any || Array.IndexOf(allowed, part) >= 0;

    private static void CheckUnknownFields(JsonElement element, string path, HashSet<string> allowed, List<LayoutIssue> issues)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                issues.Add(new LayoutIssue($"{path}/{property.Name}", "unknown-field", "Unknown field."));
            }
        }
    }

    private static void ScanStrings(JsonElement element, string path, List<LayoutIssue> issues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = $"{path}/{property.Name}";
                    CheckText(property.Name, childPath, issues);
                    ScanStrings(property.Value, childPath, issues);
                }

                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ScanStrings(item, $"{path}/{i++}", issues);
                }

                break;
            case JsonValueKind.String:
                CheckText(element.GetString() ?? string.Empty, path, issues);
                break;
        }
    }

    private static void CheckText(string text, string path, List<LayoutIssue> issues)
    {
        foreach (string fragment in _forbiddenFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LayoutIssue(path, "forbidden-content", "URLs, markup, scripts and code are not allowed in a layout."));
                return;
            }
        }
    }
}
