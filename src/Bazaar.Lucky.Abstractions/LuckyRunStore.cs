using System.Collections.Immutable;

namespace Bazaar.Lucky;

/// <summary>A run as stored, with its project, owner and GC (docs/design/26-fun-systems.md, section 6).</summary>
/// <param name="Run">The run.</param>
/// <param name="ProjectId">The project the run belongs to.</param>
/// <param name="OwnerId">The owner who started it.</param>
/// <param name="RunDay">The UTC day the run started, which the daily cap counts.</param>
/// <param name="GcId">The brainstorm GC, once the run has been applied.</param>
/// <param name="IdeaMessages">The stored message id of each idea that reached the GC, by idea index.</param>
public sealed record LuckyStoredRun(
    LuckyRun Run,
    Guid ProjectId,
    Guid OwnerId,
    DateOnly RunDay,
    Guid? GcId = null,
    ImmutableDictionary<int, Guid>? IdeaMessages = null)
{
    /// <summary>The stored message ids, never null.</summary>
    public ImmutableDictionary<int, Guid> Messages => IdeaMessages ?? ImmutableDictionary<int, Guid>.Empty;
}

/// <summary>Persists Lucky runs and answers the daily cap. The host implements it on the <c>lucky_run</c> table.</summary>
public interface ILuckyRunStore
{
    /// <summary>How many runs the owner started on <paramref name="day"/> (UTC).</summary>
    Task<int> CountStartedOnAsync(Guid ownerId, DateOnly day, CancellationToken ct);

    /// <summary>Inserts the run, or replaces it when its id is already stored.</summary>
    Task SaveAsync(LuckyStoredRun stored, CancellationToken ct);

    /// <summary>Loads a run by id, or null when there is none.</summary>
    Task<LuckyStoredRun?> LoadAsync(string runId, CancellationToken ct);
}
