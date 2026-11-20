namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Storage port for the current hire state of each Talent template. The host implements it (the owning doc 25 store).
/// It keeps one row per template: the state of the latest hire vote.
/// </summary>
public interface IHireDecisionStore
{
    /// <summary>Loads the current state for a template, or null when no hire vote was ever opened for it.</summary>
    Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct);

    /// <summary>Inserts or replaces the current state, keyed by <see cref="HireVoteState.TemplateId"/>.</summary>
    Task SaveAsync(HireVoteState state, CancellationToken ct);
}
