namespace Bazaar.Server.Hive.Abstractions;

/// <summary>
/// What a Hive Mind copy may be (docs/design/08-memory-system.md, section 6): a procedural lesson, a failure
/// lesson or a technical fact. Preferences, reputation and private memory are never Hive kinds.
/// </summary>
public enum HiveKind
{
    /// <summary>A procedural lesson.</summary>
    Procedure,

    /// <summary>A failure lesson.</summary>
    Failure,

    /// <summary>A technical fact naming no project, customer or person.</summary>
    TechnicalFact,
}

/// <summary>Wire names of <see cref="HiveKind"/>, the same strings as the <c>hive/&lt;kind&gt;/</c> folders.</summary>
public static class HiveKinds
{
    /// <summary>The folder and front matter name of a kind.</summary>
    public static string Name(HiveKind kind) => kind switch
    {
        HiveKind.Procedure => "procedure",
        HiveKind.Failure => "failure",
        HiveKind.TechnicalFact => "technical-fact",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Hive kind."),
    };

    /// <summary>Parses a wire name without throwing.</summary>
    public static bool TryParse(string? text, out HiveKind kind)
    {
        foreach (var candidate in Enum.GetValues<HiveKind>())
        {
            if (string.Equals(Name(candidate), text, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
