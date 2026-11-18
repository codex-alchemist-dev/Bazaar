namespace Bazaar.Client.Athanor.Sources;

/// <summary>
/// The two sides of a save conflict for a diff view (doc 57 §5). <see cref="Original"/> is the server copy and
/// <see cref="Modified"/> is the user's copy, so the diff reads "what the server has" against "what you typed".
/// </summary>
public sealed record ConflictDiff(string Original, string Modified)
{
    /// <summary>Builds the diff sides from a conflict and the user's unsaved content.</summary>
    public static ConflictDiff From(SaveOutcome.Conflict conflict, string mine)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(mine);

        return new ConflictDiff(conflict.ServerContent, mine);
    }
}
