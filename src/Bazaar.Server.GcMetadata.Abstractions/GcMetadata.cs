namespace Bazaar.Server.GcMetadata;

/// <summary>The reserved keys of <c>gc.metadata</c> that have a typed reading (docs/design/01-gc-data-model.md, GC metadata). Every other key is free-form.</summary>
public static class GcMetadataKeys
{
    /// <summary>Slug of the project the GC belongs to.</summary>
    public const string Project = "project";

    /// <summary>Slug naming what the GC is for, finer than its type key.</summary>
    public const string Kind = "kind";

    /// <summary>One of <see cref="GcMetadataSchema.AllowedStatuses"/>.</summary>
    public const string Status = "status";

    /// <summary>The three typed keys.</summary>
    public static IReadOnlyList<string> Typed { get; } = [Project, Kind, Status];
}

/// <summary>Operator-set rules for the typed keys. The shipped default is <see cref="Default"/>.</summary>
/// <param name="AllowedStatuses">The accepted <c>status</c> values (lower-case slugs). Must not be empty.</param>
public sealed record GcMetadataSchema(IReadOnlyList<string> AllowedStatuses)
{
    /// <summary>Longest accepted <c>project</c> slug.</summary>
    public const int MaxProjectLength = 64;

    /// <summary>Longest accepted <c>kind</c> slug.</summary>
    public const int MaxKindLength = 40;

    /// <summary>The shipped default status list: open, active, blocked, done.</summary>
    public static GcMetadataSchema Default { get; } = new(["open", "active", "blocked", "done"]);
}

/// <summary>One reason a typed value was refused.</summary>
/// <param name="Key">The metadata key.</param>
/// <param name="Message">Plain-language reason.</param>
public sealed record GcMetadataIssue(string Key, string Message);

/// <summary>The typed reading of a GC's metadata: the three typed keys, every other key untouched, and the problems found.</summary>
/// <param name="Project">The project slug, or null when absent or invalid.</param>
/// <param name="Kind">The kind slug, or null when absent or invalid.</param>
/// <param name="Status">The status, or null when absent or invalid.</param>
/// <param name="Other">Keys that are not typed, unchanged.</param>
/// <param name="Issues">Typed keys that are present but invalid.</param>
public sealed record GcMetadataView(
    string? Project,
    string? Kind,
    string? Status,
    IReadOnlyDictionary<string, string> Other,
    IReadOnlyList<GcMetadataIssue> Issues);

/// <summary>A change to the typed keys. Null leaves a key unchanged; an empty string removes it.</summary>
public sealed record GcMetadataPatch(string? Project = null, string? Kind = null, string? Status = null);

/// <summary>Result of applying a <see cref="GcMetadataPatch"/>.</summary>
/// <param name="Metadata">The full metadata to store, or null when the patch was refused.</param>
/// <param name="Issues">Why it was refused. Empty on success.</param>
public sealed record GcMetadataApplyResult(IReadOnlyDictionary<string, string>? Metadata, IReadOnlyList<GcMetadataIssue> Issues)
{
    /// <summary>True when the patch was accepted.</summary>
    public bool Succeeded => Metadata is not null;
}

/// <summary>The header of one GC that readers outside the GC module need: its type key and raw metadata.</summary>
/// <param name="TypeKey">The <c>gc_type</c> registry key, such as <c>project</c> or <c>dm</c>.</param>
/// <param name="Metadata">The <c>gc.metadata</c> map, unchanged. Read the typed keys with <see cref="GcMetadataKeys"/>.</param>
public sealed record GcHeader(string TypeKey, IReadOnlyDictionary<string, string> Metadata);

/// <summary>Read-only access to a GC's type key and metadata by id (docs/design/01-gc-data-model.md, GC metadata).</summary>
public interface IGcHeaderSource
{
    /// <summary>Finds the header of the GC, or null when there is no such GC.</summary>
    Task<GcHeader?> FindAsync(Guid gcId, CancellationToken ct);
}
