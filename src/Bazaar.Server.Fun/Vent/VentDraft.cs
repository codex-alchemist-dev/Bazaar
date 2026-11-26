namespace Bazaar.Server.Fun.Vent;

/// <summary>Where a draft sits in its life (doc 26, section 2).</summary>
public enum VentStatus
{
    /// <summary>Written and private.</summary>
    Draft,

    /// <summary>Held for the owner: the source was hard-tainted, or soft-tainted from a tainted source.</summary>
    Held,

    /// <summary>Published to its target after an owner approval.</summary>
    Published,
}

/// <summary>
/// A vent draft: what happened, what it would change, what it is unsure of (doc 26, section 2). The shape is fixed:
/// the sanitized fields are the only text that may leave the agent's private GC.
/// </summary>
/// <param name="FailureRef">The failed card or review reference. Keys the draft, so a restart writes no second copy.</param>
/// <param name="AgentId">The agent that wrote the vent.</param>
/// <param name="WhatHappened">The sanitized account, by GC sequence reference.</param>
/// <param name="WhatWouldChange">The sanitized change the agent would make.</param>
/// <param name="Unsure">The sanitized open doubts.</param>
/// <param name="Status">The draft's status.</param>
/// <param name="PublishTarget">The target chosen when it is published; null until then.</param>
public sealed record VentDraft(
    string FailureRef,
    string AgentId,
    string WhatHappened,
    string WhatWouldChange,
    string Unsure,
    VentStatus Status,
    string? PublishTarget = null);
