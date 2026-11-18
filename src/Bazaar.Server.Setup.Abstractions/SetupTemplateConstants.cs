namespace Bazaar.Server.Setup;

/// <summary>
/// Names, versions and size limits for company templates (doc 45 section 1.8). The limits are safe shipped defaults, not operator
/// settings in this slice. They are checked before a file is read in full, so an oversize template cannot exhaust memory.
/// </summary>
public static class SetupTemplateConstants
{
    /// <summary>The only <c>schemaVersion</c> of <c>template.json</c> this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The manifest file at the root of a template folder.</summary>
    public const string ManifestFileName = "template.json";

    /// <summary>The largest <c>template.json</c> accepted, in bytes.</summary>
    public const int MaxManifestBytes = 256 * 1024;

    /// <summary>The largest single seed file accepted, in bytes.</summary>
    public const int MaxSeedBytes = 256 * 1024;

    /// <summary>The largest total of <c>template.json</c> and every seed file, in bytes.</summary>
    public const int MaxTotalBytes = 4 * 1024 * 1024;

    /// <summary>The most seed files one template may name.</summary>
    public const int MaxSeedFiles = 64;

    /// <summary>The most roles, GC types, Soul presets or modules one template may list.</summary>
    public const int MaxListEntries = 64;

    /// <summary>The most presets one calendar or quota section may hold, and the most keys one preset may hold.</summary>
    public const int MaxPresets = 64;

    /// <summary>The Kanban columns used when a template does not list any (doc 45 section 1.8).</summary>
    public static readonly string[] DefaultKanbanColumns = ["Backlog", "Ready", "In progress", "Review", "Done"];

    /// <summary>The status that Kanban marks automatically (doc 12). A template may not list it as a column.</summary>
    public const string BlockedColumn = "Blocked";
}
