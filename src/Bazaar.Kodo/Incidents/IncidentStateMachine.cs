namespace Bazaar.Kodo.Incidents;

/// <summary>
/// The incident lifecycle table (doc 46 section 16.3). Pure: it only says which moves exist. Who may make a move (the enter and exit
/// controls, the approval gate, the stability window) is checked by <see cref="IncidentController"/>.
/// </summary>
public static class IncidentStateMachine
{
    private static readonly Dictionary<IncidentPhase, IncidentPhase[]> _moves = new()
    {
        [IncidentPhase.Opened] = [IncidentPhase.Frozen],
        [IncidentPhase.Frozen] = [IncidentPhase.Investigating],
        [IncidentPhase.Investigating] = [IncidentPhase.Recovering, IncidentPhase.Resolved],
        [IncidentPhase.Recovering] = [IncidentPhase.Investigating, IncidentPhase.Resolved],
        [IncidentPhase.Resolved] = [IncidentPhase.Investigating, IncidentPhase.Closed],
        [IncidentPhase.Closed] = [],
    };

    /// <summary>True when <paramref name="from"/> may move to <paramref name="to"/>. A phase never moves to itself.</summary>
    public static bool IsValid(IncidentPhase from, IncidentPhase to) =>
        _moves.TryGetValue(from, out IncidentPhase[]? targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>The phases reachable from <paramref name="from"/>.</summary>
    public static IReadOnlyList<IncidentPhase> Next(IncidentPhase from) =>
        _moves.TryGetValue(from, out IncidentPhase[]? targets) ? targets : [];
}
