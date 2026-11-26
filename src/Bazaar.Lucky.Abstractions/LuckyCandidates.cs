namespace Bazaar.Lucky;

/// <summary>Opt-in state, owned by Lucky (docs/design/26-fun-systems.md, section 6). The host implements it on <c>lucky_opt_in</c>.</summary>
public interface ILuckyOptInStore
{
    /// <summary>Sets an agent's opt-in. Only the owner calls this; the caller checks.</summary>
    Task SetAsync(Guid agentId, bool optedIn, Guid setBy, CancellationToken ct);
}

/// <summary>Lists the agents a Lucky run may invite. The host builds it from the agent table and <c>lucky_opt_in</c>.</summary>
public interface ILuckyCandidateSource
{
    /// <summary>
    /// The non-retired agents. <see cref="LuckyCandidate.OnShift"/> is true for an active agent; until the doc 09 schedule exists,
    /// "on shift" means active rather than resting. <see cref="LuckyCandidate.OptedIn"/> is the owner's setting, false by default.
    /// </summary>
    Task<IReadOnlyList<LuckyCandidate>> ListAsync(CancellationToken ct);
}
