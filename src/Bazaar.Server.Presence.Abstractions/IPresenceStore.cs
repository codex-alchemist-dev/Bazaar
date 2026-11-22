namespace Bazaar.Server.Presence.Abstractions;

/// <summary>Where presence rows live. Postgres <c>agent_activity</c> is the truth when it exists (doc 23); other stores are caches of it.</summary>
public interface IPresenceStore
{
    /// <summary>Stores <paramref name="row"/> when its revision is greater than the stored one for that agent.</summary>
    /// <returns>True when stored; false when a row with an equal or higher revision was already held.</returns>
    ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct);

    /// <summary>One agent's row, or null.</summary>
    ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct);

    /// <summary>Every agent's row.</summary>
    ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct);
}
