namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary>
/// Where the deterministic admission triggers other than the loop guard reach the ward (doc 26, section 1). Callers report facts;
/// the intake applies the rule and admits through <see cref="IPsychWardService"/>, so admission mode, the one-stay rule and
/// restrictions all apply. There is deliberately no entry for a classifier hit: a classifier alone never admits.
/// </summary>
public interface IWardTriggerIntake
{
    /// <summary>
    /// Reports an agent's most recent outcomes on one tag, newest first, true meaning failed (doc 11 outcomes: CI, review, owner
    /// reject, revert). Admits with <see cref="WardTrigger.RepeatedFailure"/> when the newest <c>ward.repeated_failure_count</c>
    /// outcomes all failed. A window shorter than that count never admits.
    /// </summary>
    Task<WardOutcome?> ReportOutcomesAsync(Guid agentId, string tag, IReadOnlyList<bool> recentFailedNewestFirst, CancellationToken cancellationToken);

    /// <summary>A planted canary was used (doc 15). Always deterministic, so it admits with <see cref="WardTrigger.DeniedCallOrCanary"/>.</summary>
    Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken cancellationToken);

    /// <summary>
    /// Reports denied calls counted by the caller over its own window. Admits with <see cref="WardTrigger.DeniedCallOrCanary"/>
    /// once the count reaches <c>ward.denied_call_threshold</c>; returns null below it.
    /// </summary>
    Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken cancellationToken);

    /// <summary>A shared token limit (doc 15) hard-stopped the agent. Admits with <see cref="WardTrigger.TokenHardStop"/>.</summary>
    Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken cancellationToken);

    /// <summary>The agent asks for admission itself.</summary>
    Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken cancellationToken);

    /// <summary>The owner refers the agent. Always allowed, even when admission is owner-only.</summary>
    Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken cancellationToken);
}
