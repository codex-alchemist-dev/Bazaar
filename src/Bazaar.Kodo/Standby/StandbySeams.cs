namespace Bazaar.Kodo.Standby;

/// <summary>A witness's answer to a lease request.</summary>
/// <param name="Granted">True when the witness granted the lease.</param>
/// <param name="HighestEpochSeen">The highest epoch the witness has granted or seen.</param>
public sealed record GrantResponse(bool Granted, long HighestEpochSeen);

/// <summary>
/// One witness: an owner's paired device or the optional third node. A witness holds the highest epoch it has granted and never
/// grants a lower or equal epoch to a different holder, nor any epoch while the lease it holds is unexpired by its own clock.
/// The implementation may throw, time out or never answer; the controller counts that as no answer.
/// </summary>
public interface IStandbyWitness
{
    /// <summary>The witness id, as listed in the <see cref="WitnessQuorumPolicy"/>.</summary>
    string Id { get; }

    /// <summary>Reports whether the witness sees the primary and the newest lease it knows.</summary>
    ValueTask<WitnessReport> ObserveAsync(string primaryId, CancellationToken cancellationToken);

    /// <summary>Asks the witness to grant (or renew) <paramref name="lease"/>.</summary>
    ValueTask<GrantResponse> RequestGrantAsync(LeaseRecord lease, CancellationToken cancellationToken);
}

/// <summary>
/// The journal seam. <paramref name="kind"/> and <paramref name="payload"/> are what the Kodo journal chains into a
/// <see cref="Bazaar.Kodo.Journal.JournalRecord"/>; the payload is the canonical <see cref="StandbyState"/>, so the epoch is in every
/// record. The implementation throws <see cref="IOException"/> when it cannot write.
/// </summary>
public interface IStandbyJournal
{
    /// <summary>Appends one entry.</summary>
    ValueTask AppendAsync(DateTimeOffset at, string kind, byte[] payload, CancellationToken cancellationToken);
}

/// <summary>The journal kinds the standby writes.</summary>
public static class StandbyJournalKinds
{
    /// <summary>The standby took the lease.</summary>
    public const string Promoted = "standby.promoted";

    /// <summary>A promotion was cancelled after witnesses granted but before it took effect.</summary>
    public const string PromotionAborted = "standby.promotion_aborted";

    /// <summary>The node fenced itself.</summary>
    public const string Fenced = "standby.fenced";

    /// <summary>A person returned a fenced node to standby.</summary>
    public const string Rejoined = "standby.rejoined";

    /// <summary>A write with a stale or future token was rejected.</summary>
    public const string StaleRejected = "standby.stale_rejected";
}

/// <summary>Persists the node's state so a restart cannot forget its epoch. The implementation throws <see cref="IOException"/> on failure.</summary>
public interface IStandbyStateStore
{
    /// <summary>Loads the saved state, or null when none was ever saved.</summary>
    ValueTask<StandbyState?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves the state, replacing the old one.</summary>
    ValueTask SaveAsync(StandbyState state, CancellationToken cancellationToken);
}
