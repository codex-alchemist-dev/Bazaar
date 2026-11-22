using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// A company template (doc 45 section 1.8): a versioned folder that is an input to the setup plan, never a copy of a live
/// company. It holds structure and presets only. The loader rejects secrets and references to them, GC messages, Soul
/// instances and memories, personal data, an owner identity, and provider or GitHub account keys (see
/// <see cref="TemplateContentRules"/>).
/// </summary>
/// <remarks>
/// Lists that are sets (modules, GC types, Soul presets, roles, role charter paths) are normalized to sorted, distinct order when
/// the template is loaded, so the order in the file does not change the template hash. Kanban columns keep their order.
/// A template has no field that activates an agent: a Soul preset is behavioural only (doc 07), and the Constitution must be
/// accepted before any Soul activates (doc 24).
/// </remarks>
public sealed class SetupTemplate
{
    /// <summary>The template schema version. This build reads only <see cref="SetupTemplateConstants.SchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; } = SetupTemplateConstants.SchemaVersion;

    /// <summary>An exact semantic version, such as <c>1.0.0</c>. Ranges are not supported.</summary>
    public string Version { get; init; } = "";

    /// <summary>The template id: lower-case letters, digits and dashes. Matches the id in <c>company.source</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Required. Module names such as <c>Bazaar.GC</c>. Merged into the plan's module set.</summary>
    public List<string> Modules { get; init; } = [];

    /// <summary>Install-scope settings presets keyed by registry name (doc 03). Validated by the same registry as plan settings.</summary>
    public Dictionary<string, JsonElement> Settings { get; init; } = [];

    /// <summary>Role definitions, including custom roles. Permissions are opaque strings with no behaviour attached here.</summary>
    public List<SetupTemplateRole> Roles { get; init; } = [];

    /// <summary>GC type names to register (doc 01). Names only.</summary>
    public List<string> GcTypes { get; init; } = [];

    /// <summary>Kanban columns in order. Blocked is marked automatically (doc 12) and is never a listed column.</summary>
    public List<string> KanbanColumns { get; init; } = [.. SetupTemplateConstants.DefaultKanbanColumns];

    /// <summary>Named calendar presets (doc 24). Each holds opaque scalar values; only the shape is checked.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> CalendarPresets { get; init; } = [];

    /// <summary>Named quota presets (doc 09). Each holds opaque scalar values; only the shape is checked.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> QuotaPresets { get; init; } = [];

    /// <summary>Behavioural Soul preset ids to offer (doc 07). A preset never activates an agent.</summary>
    public List<string> SoulPresets { get; init; } = [];

    /// <summary>The Markdown seed files that become Vault files when the template is applied (doc 18).</summary>
    public SetupTemplateSeeds Seeds { get; init; } = new();
}

/// <summary>One role in a template. Custom roles use the same shape. The permissions have no meaning to the loader.</summary>
public sealed class SetupTemplateRole
{
    /// <summary>A lower-case slug, unique within the template.</summary>
    public string Id { get; init; } = "";

    /// <summary>The name shown to people. One to 100 characters.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Opaque permission strings, distinct. The authorization model (doc 15) interprets them later, not this slice.</summary>
    public List<string> Permissions { get; init; } = [];
}

/// <summary>The seed files a template names, each a relative path inside the template folder. Every seed is optional.</summary>
public sealed class SetupTemplateSeeds
{
    /// <summary>A relative Markdown path to the Constitution draft (doc 24).</summary>
    public string? ConstitutionDraft { get; init; }

    /// <summary>Relative Markdown paths to role charters. Normalized to sorted, distinct order.</summary>
    public List<string> RoleCharters { get; init; } = [];

    /// <summary>A relative Markdown path that describes the Vault folder layout (doc 18).</summary>
    public string? VaultLayout { get; init; }
}
