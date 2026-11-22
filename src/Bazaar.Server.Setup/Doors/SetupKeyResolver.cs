using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Bazaar.Server.Setup;

/// <summary>What a resolved key holds. Chosen from the source-generated metadata of <see cref="SetupPlan"/>, never by reflection.</summary>
internal enum SetupLeafKind
{
    /// <summary>A string.</summary>
    Text,

    /// <summary>A whole number (int).</summary>
    Integer,

    /// <summary>A bool.</summary>
    Boolean,

    /// <summary>A list of strings. The whole list is one JSON array; one item may also be set by index.</summary>
    StringList,

    /// <summary>A list of sections, such as models.sources. The whole list is one JSON array of objects.</summary>
    ObjectList,

    /// <summary>A settings-registry value, typed by the registry's value type.</summary>
    Setting,
}

/// <summary>One step of a key path: a property name, or an index into a list.</summary>
internal readonly record struct SetupPathStep(string? Name, int Index)
{
    public bool IsIndex => Name is null;
}

/// <summary>A key resolved against the plan's shape.</summary>
/// <param name="Steps">The path from the root of the plan document.</param>
/// <param name="Kind">The kind of value the key holds.</param>
/// <param name="Setting">The registry entry for a settings key; otherwise null.</param>
internal sealed record SetupKeyTarget(IReadOnlyList<SetupPathStep> Steps, SetupLeafKind Kind, ISetupSettingDefinition? Setting)
{
    /// <summary>The path as messages print it: names joined by dots, list items as [n].</summary>
    public string Dotted => SetupKeyResolver.DottedOf(Steps);

    /// <summary>The path with list items as [], the form the nullable and secret rules are written in.</summary>
    public string Template => SetupKeyResolver.TemplateOf(Steps);
}

/// <summary>
/// Resolves dotted key paths against the shape of the setup plan, and types the values written for them (doc 45 sections 1.2
/// and 1.6). Names match case-insensitively and are written in their camelCase form. A list takes a numeric index. The
/// <c>settings</c> block takes registry keys, which contain dots, so the rest of the path after <c>settings</c> is one key.
/// Every message names the path and the rule, and never a value.
/// </summary>
internal static class SetupKeyResolver
{
    private const string _nullWord = "null";
    private const string _keyRefName = "keyRef";
    private const string _emptyPathText = "(empty)";
    private const string _bootstrapRule =
        "must be a bootstrap reference: env:NAME, file:PATH, dpapi:PATH or systemd-creds:NAME. A literal value is not accepted; this key holds references only.";
    private const string _secretStoreRule =
        "must be a secret://bazaar/ reference. A literal value is not accepted; this key holds references only.";

    /// <summary>The reference families each secret-typed key accepts, by its template path (doc 45 section 1.6).</summary>
    private static readonly Dictionary<string, (SecretReferenceFamily[] Families, string Rule)> _secretKeys = new(StringComparer.Ordinal)
    {
        ["custody.kekRef"] = (SecretReferences.BootstrapFamilies, _bootstrapRule),
        ["custody.mergeCredential.appIdRef"] = (SecretReferences.SecretStoreFamilies, _secretStoreRule),
        ["models.sources[].keyRef"] = (SecretReferences.SecretStoreFamilies, _secretStoreRule),
    };

    /// <summary>
    /// Resolves the segments of a key path. The plan's own metadata drives the walk, so a new property on <see cref="SetupPlan"/> is
    /// settable without a second list. The typed path appears in every error, exactly as the operator wrote it.
    /// </summary>
    public static (SetupKeyTarget? Target, SetupValidationIssue? Issue) Resolve(IReadOnlyList<string> segments, ISetupSettingCatalog registry)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(registry);

        var typed = string.Join('.', segments);
        if (segments.Count == 0 || segments.Any(segment => segment.Length == 0))
        {
            return Fail(typed, "is not a key path. Use camelCase names separated by dots, for example install.companyName.");
        }

        var steps = new List<SetupPathStep>();
        var info = InfoOf(typeof(SetupPlan));
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var last = i == segments.Count - 1;
            if (info is null)
            {
                return Fail(typed, "has no keys below it.");
            }

            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                // Only the settings block is a dictionary. Its keys are registry names, and those contain dots, so the rest is one key.
                return ResolveSetting(string.Join('.', segments.Skip(i)), typed, steps, registry);
            }

            if (info.Kind == JsonTypeInfoKind.Enumerable)
            {
                if (!TryIndex(segment, out var index))
                {
                    return Fail(typed, "must be a list index, a whole number from 0, at this point.");
                }

                steps.Add(new SetupPathStep(null, index));
                var element = info.ElementType!;
                if (last)
                {
                    return LeafOf(element, steps, typed);
                }

                info = InfoOf(element);
                continue;
            }

            var property = info.Properties.FirstOrDefault(p => string.Equals(p.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                return Fail(typed, "is not a known setup key.");
            }

            steps.Add(new SetupPathStep(property.Name, 0));
            if (last)
            {
                return LeafOf(property.PropertyType, steps, typed);
            }

            info = InfoOf(property.PropertyType);
        }

        // Every path that reaches here ended on a section, which is not a value.
        return Fail(typed, "is a section; set one of its keys instead.");
    }

    /// <summary>Types the text of a value for the key (doc 45 section 1.6 for the secret rule). The node is null for a JSON null.</summary>
    public static (JsonNode? Node, SetupValidationIssue? Issue) ToNode(SetupKeyTarget target, string text)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);

        var path = target.Dotted;
        switch (target.Kind)
        {
            case SetupLeafKind.Text:
                if (text == _nullWord && SetupPlanLoader.NullablePaths.Contains(target.Template))
                {
                    return (null, null);
                }

                if (_secretKeys.TryGetValue(target.Template, out var rule) && !SecretReferences.IsValid(text, rule.Families))
                {
                    return (null, Error(path, rule.Rule));
                }

                return (JsonValue.Create(text), null);

            case SetupLeafKind.Integer:
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    return (null, Error(path, "must be a whole number."));
                }

                return (JsonValue.Create(whole), null);

            case SetupLeafKind.Boolean:
                if (!bool.TryParse(text, out var flag))
                {
                    return (null, Error(path, "must be true or false."));
                }

                return (JsonValue.Create(flag), null);

            case SetupLeafKind.StringList:
                var strings = ParseArray(text);
                if (strings is null || !strings.All(item => item is JsonValue value && value.TryGetValue<string>(out _)))
                {
                    return (null, Error(path, "must be a JSON array of strings, for example [\"Bazaar.GC\"]."));
                }

                return (strings, null);

            case SetupLeafKind.ObjectList:
                var items = ParseArray(text);
                if (items is null)
                {
                    return (null, Error(path, "must be a JSON array of objects."));
                }

                // A reference inside a whole-list value is checked here too, so a literal cannot enter through a list.
                if (_secretKeys.TryGetValue(target.Template + "[]." + _keyRefName, out var itemRule))
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i] is JsonObject item
                            && item[_keyRefName] is JsonValue keyRef
                            && keyRef.TryGetValue<string>(out var keyRefText)
                            && !SecretReferences.IsValid(keyRefText, itemRule.Families))
                        {
                            return (null, Error($"{path}[{i}].{_keyRefName}", itemRule.Rule));
                        }
                    }
                }

                return (items, null);

            case SetupLeafKind.Setting:
                return ConvertSetting(target.Setting!, path, text);

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Unknown leaf kind.");
        }
    }

    /// <summary>
    /// Writes a typed value at its path in the document. Missing sections and list items are created on the way, so a document
    /// that has only some of the keys gets the rest from the doors. A value where a section is needed is an error, not a replacement.
    /// </summary>
    public static SetupValidationIssue? Put(JsonObject root, SetupKeyTarget target, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);

        JsonNode? current = root;
        var steps = target.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var last = i == steps.Count - 1;
            if (!step.IsIndex)
            {
                if (current is not JsonObject obj)
                {
                    return BlockedBy(target);
                }

                if (last)
                {
                    obj[step.Name!] = node;
                    return null;
                }

                current = obj[step.Name!] ?? (obj[step.Name!] = NewContainer(steps[i + 1]));
                continue;
            }

            if (current is not JsonArray array)
            {
                return BlockedBy(target);
            }

            if (step.Index > array.Count)
            {
                return Error(target.Dotted, "is past the end of its list. List items must be given in order, starting at 0.");
            }

            if (last)
            {
                if (step.Index == array.Count)
                {
                    array.Add(node);
                }
                else
                {
                    array[step.Index] = node;
                }

                return null;
            }

            if (step.Index == array.Count)
            {
                array.Add(NewContainer(steps[i + 1]));
            }

            current = array[step.Index];
        }

        return null;
    }

    /// <summary>The dotted form of a path. Items are shown as [n].</summary>
    public static string DottedOf(IReadOnlyList<SetupPathStep> steps)
    {
        var text = new StringBuilder();
        foreach (var step in steps)
        {
            if (step.IsIndex)
            {
                text.Append('[').Append(step.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('.');
            }

            text.Append(step.Name);
        }

        return text.ToString();
    }

    /// <summary>The template form of a path. Items are shown as [], so the nullable and secret rules can name them.</summary>
    public static string TemplateOf(IReadOnlyList<SetupPathStep> steps)
    {
        var text = new StringBuilder();
        foreach (var step in steps)
        {
            if (step.IsIndex)
            {
                text.Append("[]");
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('.');
            }

            text.Append(step.Name);
        }

        return text.ToString();
    }

    private static (SetupKeyTarget? Target, SetupValidationIssue? Issue) ResolveSetting(string key, string typed, List<SetupPathStep> steps, ISetupSettingCatalog registry)
    {
        var definition = registry.Definitions.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return Fail(typed, "is not a known setting key in this build.");
        }

        steps.Add(new SetupPathStep(definition.Name, 0));
        return (new SetupKeyTarget(steps.ToArray(), SetupLeafKind.Setting, definition), null);
    }

    /// <summary>The leaf for a property or list item. Scalars are typed; a list is one JSON array; a section is not a value.</summary>
    private static (SetupKeyTarget? Target, SetupValidationIssue? Issue) LeafOf(Type type, List<SetupPathStep> steps, string typed)
    {
        var scalar = ScalarKindOf(type);
        if (scalar is not null)
        {
            return (new SetupKeyTarget(steps.ToArray(), scalar.Value, null), null);
        }

        var info = InfoOf(type);
        if (info is not null && info.Kind == JsonTypeInfoKind.Enumerable)
        {
            var kind = info.ElementType == typeof(string) ? SetupLeafKind.StringList : SetupLeafKind.ObjectList;
            return (new SetupKeyTarget(steps.ToArray(), kind, null), null);
        }

        return Fail(typed, "is a section; set one of its keys instead.");
    }

    /// <summary>The kind of a scalar type, or null when the type is not a scalar.</summary>
    private static SetupLeafKind? ScalarKindOf(Type type)
    {
        if (type == typeof(string))
        {
            return SetupLeafKind.Text;
        }

        if (type == typeof(int))
        {
            return SetupLeafKind.Integer;
        }

        return type == typeof(bool) ? SetupLeafKind.Boolean : null;
    }

    /// <summary>The source-generated metadata of a type. Null for scalars, which have no keys below them.</summary>
    private static JsonTypeInfo? InfoOf(Type type) =>
        ScalarKindOf(type) is null ? SetupPlanJsonContext.Default.GetTypeInfo(type) : null;

    private static bool TryIndex(string segment, out int index)
    {
        index = 0;
        return segment.Length > 0
            && segment.All(char.IsAsciiDigit)
            && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static (JsonNode? Node, SetupValidationIssue? Issue) ConvertSetting(ISetupSettingDefinition definition, string path, string text)
    {
        var type = definition.ValueType;
        if (type == typeof(string) || type.IsEnum)
        {
            return (JsonValue.Create(text), null);
        }

        if (type == typeof(bool))
        {
            return bool.TryParse(text, out var flag) ? (JsonValue.Create(flag), null) : (null, Error(path, "must be true or false."));
        }

        if (type == typeof(int))
        {
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? (JsonValue.Create(whole), null)
                : (null, Error(path, "must be a whole number."));
        }

        // Lists and any other registry type take their JSON form. The registry checks the shape when the plan is validated.
        try
        {
            return (JsonNode.Parse(text), null);
        }
        catch (JsonException)
        {
            return (null, Error(path, $"must be a JSON value of type {type.Name}."));
        }
    }

    /// <summary>Parses a JSON array, or returns null when the text is not one.</summary>
    private static JsonArray? ParseArray(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The container a path needs at a step: an array for a list item, otherwise a section.</summary>
    private static JsonNode NewContainer(SetupPathStep next) => next.IsIndex ? new JsonArray() : new JsonObject();

    private static SetupValidationIssue BlockedBy(SetupKeyTarget target) =>
        Error(target.Dotted, "cannot be set: the document holds a value of the wrong shape at this path.");

    private static (SetupKeyTarget? Target, SetupValidationIssue? Issue) Fail(string typed, string message) =>
        (null, Error(typed.Length == 0 ? _emptyPathText : typed, message));

    private static SetupValidationIssue Error(string path, string message) =>
        new(SetupIssueSeverity.Error, path, message);
}
