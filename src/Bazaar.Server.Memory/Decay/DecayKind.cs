namespace Bazaar.Server.Memory.Decay;

/// <summary>
/// Memory kinds for decay and sweeping. Mirrors the kinds in docs/design/08-memory-system.md, section 1.
/// Defined locally so this area does not depend on the notes area.
/// </summary>
public enum DecayKind
{
    /// <summary>Episode summaries (doc 08 episodic).</summary>
    Episodic = 0,

    /// <summary>Consolidated facts (doc 08 semantic).</summary>
    Semantic = 1,

    /// <summary>Lessons and procedures (doc 08 procedural).</summary>
    Procedural = 2,

    /// <summary>Failures. Never decays below the failure floor.</summary>
    Failure = 3,

    /// <summary>Preferences.</summary>
    Preference = 4,

    /// <summary>Working notes for one task. Swept at TTL by <see cref="WorkingNoteSweeper"/>.</summary>
    Working = 5,

    /// <summary>Notes copied through Hive Mind.</summary>
    Hive = 6,
}
