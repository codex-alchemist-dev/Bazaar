using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Setup;

/// <summary>A seed file a template names: the key path that names it (for messages) and its relative path (for reading).</summary>
/// <param name="KeyPath">The JSON path of the key, such as <c>seeds.roleCharters[0]</c>.</param>
/// <param name="RelativePath">The relative path inside the template folder, such as <c>seeds/charters/reviewer.md</c>.</param>
public sealed record TemplateSeedReference(string KeyPath, string RelativePath);

/// <summary>
/// The semantic rules of <c>template.json</c> (doc 45 section 1.8). Runs after the structural walk and the typed decode. It adds
/// issues and normalizes the template in place: set-like lists are sorted and de-duplicated, and each settings value becomes the
/// canonical JSON the registry stores. Settings go through the same registry rules as plan settings. The messages name the key and
/// the rule and never repeat a value.
/// </summary>
internal static partial class TemplateValidator
{
    private const int _maxNameLength = 100;
    private const int _maxColumnLength = 40;
    private const int _maxColumns = 20;
    private const int _maxSeedPathLength = 200;
    private const int _maxPermissionsPerRole = 64;
    private const int _maxPresetValueLength = 256;
    private const int _maxPresetKeys = 64;

    /// <summary>A seed path: relative, forward-slash, one or more segments, each starting with a letter, digit, dash or underscore, ending in <c>.md</c>.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex SeedPathRegex();

    /// <summary>An opaque permission string. Its meaning belongs to the authorization model (doc 15), not to the template.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9_.:-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionRegex();

    /// <summary>A preset key: a letter, then letters, digits, dots, dashes or underscores.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PresetKeyRegex();

    /// <summary>Checks and normalizes <paramref name="template"/>, adds every issue found, and returns the seed references in a stable order.</summary>
    public static List<TemplateSeedReference> Validate(SetupTemplate template, SettingRegistry registry, List<SetupValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(issues);

        if (template.SchemaVersion != SetupTemplateConstants.SchemaVersion)
        {
            issues.Add(Error("schemaVersion", $"is not supported; this build reads schemaVersion {SetupTemplateConstants.SchemaVersion}."));
        }

        if (!SetupPlanValidator.IsTemplateId(template.Id))
        {
            issues.Add(Error("id", "must be lower-case letters, digits and dashes, for example starter."));
        }

        if (!SetupPlanValidator.IsSemver(template.Version))
        {
            issues.Add(Error("version", "must be one exact semantic version, for example 1.0.0. Version ranges are not supported."));
        }

        ValidateModules(template, issues);
        ValidateSettings(template, registry, issues);
        ValidateRoles(template, issues);
        ValidateSlugList(template.GcTypes, "gcTypes", issues);
        ValidateKanban(template, issues);
        ValidatePresets(template.CalendarPresets, "calendarPresets", issues);
        ValidatePresets(template.QuotaPresets, "quotaPresets", issues);
        ValidateSlugList(template.SoulPresets, "soulPresets", issues);

        return ValidateSeeds(template, issues);
    }

    private static void ValidateModules(SetupTemplate template, List<SetupValidationIssue> issues)
    {
        if (template.Modules.Count == 0)
        {
            issues.Add(Error("modules", "must name at least one module."));
        }

        if (template.Modules.Count > SetupTemplateConstants.MaxListEntries)
        {
            issues.Add(Error("modules", $"must name at most {SetupTemplateConstants.MaxListEntries} modules."));
        }

        for (var i = 0; i < template.Modules.Count; i++)
        {
            if (!SetupPlanValidator.IsModuleName(template.Modules[i]))
            {
                issues.Add(Error($"modules[{i}]", "must be a Bazaar module name such as Bazaar.GC."));
            }
        }

        template.Modules.RemoveAll(name => !SetupPlanValidator.IsModuleName(name));
        Normalize(template.Modules);
    }

    private static void ValidateSettings(SetupTemplate template, SettingRegistry registry, List<SetupValidationIssue> issues)
    {
        // The network block owns these two keys in a plan (doc 45 section 1.5). A template cannot set them, so there is one answer.
        foreach (var name in template.Settings.Keys.Where(IsNetworkKey).OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            issues.Add(Error("settings." + name, "is a network setting; the plan's network block sets it, not a template."));
            template.Settings.Remove(name);
        }

        SetupPlanValidator.ValidateSettingValues(template.Settings, registry, "settings.", issues);
    }

    private static bool IsNetworkKey(string name) => name is "host.status.bind" or "host.status.port";

    private static void ValidateRoles(SetupTemplate template, List<SetupValidationIssue> issues)
    {
        if (template.Roles.Count > SetupTemplateConstants.MaxListEntries)
        {
            issues.Add(Error("roles", $"must list at most {SetupTemplateConstants.MaxListEntries} roles."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < template.Roles.Count; i++)
        {
            var role = template.Roles[i];
            var path = $"roles[{i}]";
            if (!SetupPlanValidator.IsSlug(role.Id))
            {
                issues.Add(Error(path + ".id", "must be a lower-case slug, up to 63 characters."));
            }
            else if (!ids.Add(role.Id))
            {
                issues.Add(Error(path + ".id", "is already used by another role; role ids are unique."));
            }

            if (IsBlank(role.DisplayName) || role.DisplayName.Length > _maxNameLength || role.DisplayName.Any(char.IsControl))
            {
                issues.Add(Error(path + ".displayName", $"must be 1 to {_maxNameLength} characters with no control characters."));
            }

            if (role.Permissions.Count > _maxPermissionsPerRole)
            {
                issues.Add(Error(path + ".permissions", $"must list at most {_maxPermissionsPerRole} permissions."));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var p = 0; p < role.Permissions.Count; p++)
            {
                var permission = role.Permissions[p];
                if (!PermissionRegex().IsMatch(permission))
                {
                    issues.Add(Error($"{path}.permissions[{p}]", "must be a lower-case permission string, up to 64 characters."));
                }
                else if (!seen.Add(permission))
                {
                    issues.Add(Error($"{path}.permissions[{p}]", "is listed more than once."));
                }
            }

            Normalize(role.Permissions);
        }

        template.Roles.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    private static void ValidateKanban(SetupTemplate template, List<SetupValidationIssue> issues)
    {
        if (template.KanbanColumns.Count is 0 or > _maxColumns)
        {
            issues.Add(Error("kanbanColumns", $"must list 1 to {_maxColumns} columns."));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < template.KanbanColumns.Count; i++)
        {
            var column = template.KanbanColumns[i];
            var path = $"kanbanColumns[{i}]";
            if (IsBlank(column) || column.Length > _maxColumnLength || column.Any(char.IsControl))
            {
                issues.Add(Error(path, $"must be 1 to {_maxColumnLength} characters with no control characters."));
                continue;
            }

            if (string.Equals(column, SetupTemplateConstants.BlockedColumn, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error(path, "is not a column: Blocked is marked automatically (doc 12), so remove it from the list."));
                continue;
            }

            if (!seen.Add(column))
            {
                issues.Add(Error(path, "is listed more than once; column names are unique, ignoring case."));
            }
        }
    }

    /// <summary>A set of slugs: each must be a slug and listed once. Normalized to sorted order.</summary>
    private static void ValidateSlugList(List<string> values, string section, List<SetupValidationIssue> issues)
    {
        if (values.Count > SetupTemplateConstants.MaxListEntries)
        {
            issues.Add(Error(section, $"must list at most {SetupTemplateConstants.MaxListEntries} entries."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            if (!SetupPlanValidator.IsSlug(values[i] ?? string.Empty))
            {
                issues.Add(Error($"{section}[{i}]", "must be a lower-case slug, up to 63 characters."));
            }
            else if (!seen.Add(values[i]))
            {
                issues.Add(Error($"{section}[{i}]", "is listed more than once."));
            }
        }

        Normalize(values);
    }

    /// <summary>
    /// Calendar and quota presets (doc 24, doc 09). Only the shape is checked: a named preset, opaque keys, and scalar values. The
    /// meaning of each key belongs to the module that reads it.
    /// </summary>
    private static void ValidatePresets(
        Dictionary<string, Dictionary<string, JsonElement>> presets,
        string section,
        List<SetupValidationIssue> issues)
    {
        if (presets.Count > SetupTemplateConstants.MaxPresets)
        {
            issues.Add(Error(section, $"must hold at most {SetupTemplateConstants.MaxPresets} presets."));
        }

        foreach (var name in presets.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var presetPath = section + "." + name;
            if (!SetupPlanValidator.IsSlug(name))
            {
                issues.Add(Error(presetPath, "must be a lower-case slug, up to 63 characters."));
                continue;
            }

            var values = presets[name];
            if (values.Count == 0 || values.Count > _maxPresetKeys)
            {
                issues.Add(Error(presetPath, $"must hold 1 to {_maxPresetKeys} keys."));
            }

            foreach (var key in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var keyPath = presetPath + "." + key;
                if (!PresetKeyRegex().IsMatch(key))
                {
                    issues.Add(Error(keyPath, "must be a letter, then letters, digits, dots, dashes or underscores, up to 64 characters."));
                    continue;
                }

                if (!IsScalar(values[key]))
                {
                    issues.Add(Error(keyPath, "must be a string, a number or a boolean."));
                }
            }
        }
    }

    private static bool IsScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => true,
        JsonValueKind.True or JsonValueKind.False => true,
        JsonValueKind.String => (value.GetString() ?? string.Empty).Length <= _maxPresetValueLength,
        _ => false,
    };

    /// <summary>
    /// Seed paths (doc 45 section 1.8, doc 18). A path is relative, uses forward slashes, has no traversal segment, no leading
    /// slash, no drive letter, no scheme, and ends in .md. Each seed is named once. The result is in a stable order.
    /// </summary>
    private static List<TemplateSeedReference> ValidateSeeds(SetupTemplate template, List<SetupValidationIssue> issues)
    {
        var references = new List<TemplateSeedReference>();
        if (template.Seeds.ConstitutionDraft is not null)
        {
            references.Add(new TemplateSeedReference("seeds.constitutionDraft", template.Seeds.ConstitutionDraft));
        }

        Normalize(template.Seeds.RoleCharters);
        for (var i = 0; i < template.Seeds.RoleCharters.Count; i++)
        {
            references.Add(new TemplateSeedReference($"seeds.roleCharters[{i}]", template.Seeds.RoleCharters[i]));
        }

        if (template.Seeds.VaultLayout is not null)
        {
            references.Add(new TemplateSeedReference("seeds.vaultLayout", template.Seeds.VaultLayout));
        }

        if (references.Count > SetupTemplateConstants.MaxSeedFiles)
        {
            issues.Add(Error("seeds", $"may name at most {SetupTemplateConstants.MaxSeedFiles} seed files."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var problem = SeedPathProblem(reference.RelativePath);
            if (problem is not null)
            {
                issues.Add(Error(reference.KeyPath, problem));
            }
            else if (!seen.Add(reference.RelativePath))
            {
                issues.Add(Error(reference.KeyPath, "names a seed file that another key already names; each seed is named once."));
            }
        }

        return references;
    }

    /// <summary>The reason a seed path is not allowed, or null when it is. The reason never repeats the path.</summary>
    private static string? SeedPathProblem(string path)
    {
        if (IsBlank(path) || path.Length > _maxSeedPathLength)
        {
            return $"must be 1 to {_maxSeedPathLength} characters.";
        }

        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':'))
        {
            return "must be a relative path with forward slashes: no leading slash, no drive letter, no scheme.";
        }

        if (path.Split('/').Any(segment => segment == ".."))
        {
            return "must not contain '..' segments (path traversal).";
        }

        return SeedPathRegex().IsMatch(path)
            ? null
            : "must be a relative .md path of letters, digits, dashes, underscores and dots; no segment may start with a dot.";
    }

    /// <summary>Sorts and de-duplicates a set-like list in place.</summary>
    private static void Normalize(List<string> values)
    {
        var sorted = values.Where(value => value is not null).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        values.Clear();
        values.AddRange(sorted);
    }

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    private static SetupValidationIssue Error(string path, string message) =>
        new(SetupIssueSeverity.Error, path, message);
}
