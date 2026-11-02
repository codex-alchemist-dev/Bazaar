namespace Bazaar.Server.Memory.Notes;

/// <summary>Memory kinds (docs/design/08-memory-system.md, section 1).</summary>
public enum MemoryKind
{
    /// <summary>A turn summary pointing at GC messages. Written by the runtime at turn end.</summary>
    Episodic,

    /// <summary>A consolidated fact. Written only by consolidation.</summary>
    Semantic,

    /// <summary>A lesson or procedure.</summary>
    Procedural,

    /// <summary>A failure. Never deleted; decay has a floor.</summary>
    Failure,

    /// <summary>A preference of an agent or the owner.</summary>
    Preference,

    /// <summary>Task-scoped notes, swept at TTL.</summary>
    Working,

    /// <summary>A copy in Hive Mind, written by the copy flow.</summary>
    Hive,
}

/// <summary>Lifecycle status in front matter. Deprecated notes drop out of retrieval but stay on disk.</summary>
public enum MemoryNoteStatus
{
    /// <summary>Eligible for retrieval.</summary>
    Active,

    /// <summary>Excluded from retrieval; the file stays.</summary>
    Deprecated,
}

/// <summary>Where a note lives: an agent's private area, a project, company memory or Hive Mind.</summary>
public enum MemoryScope
{
    /// <summary>The agent's own <c>agents/&lt;agent&gt;/memory/</c> area.</summary>
    Private,

    /// <summary>A project's <c>projects/&lt;slug&gt;/memory/</c> area.</summary>
    Project,

    /// <summary>The <c>company/memory/</c> area.</summary>
    Company,

    /// <summary>The <c>hive/&lt;kind&gt;/</c> area.</summary>
    Hive,
}

/// <summary>Maps memory kinds to the note <c>type</c> strings of doc 08, section 1.</summary>
public static class MemoryNoteTypes
{
    /// <summary>The note type for a kind.</summary>
    public static string TypeFor(MemoryKind kind) => kind switch
    {
        MemoryKind.Episodic => "memory-episode",
        MemoryKind.Semantic => "memory-semantic",
        MemoryKind.Procedural => "memory-procedure",
        MemoryKind.Failure => "memory-failure",
        MemoryKind.Preference => "memory-preference",
        MemoryKind.Working => "memory-working",
        MemoryKind.Hive => "memory-hive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory kind."),
    };

    /// <summary>The kind a note type stands for, if the type is a memory type.</summary>
    public static bool TryKindFor(string type, out MemoryKind kind)
    {
        foreach (var candidate in Enum.GetValues<MemoryKind>())
        {
            if (string.Equals(TypeFor(candidate), type, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
