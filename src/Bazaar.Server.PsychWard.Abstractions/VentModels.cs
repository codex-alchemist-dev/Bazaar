using Bazaar.Sanitization;

namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary>Where a vent sits in its life (doc 26, section 2).</summary>
public enum VentStatus
{
    /// <summary>Written and private.</summary>
    Draft,

    /// <summary>Held for the owner: the source was hard-tainted, or soft-tainted from a tainted source session.</summary>
    Held,

    /// <summary>Published to its target after an owner approval.</summary>
    Published,
}

/// <summary>The raw fields of a vent before sanitizing.</summary>
/// <param name="WhatHappened">What happened, by GC sequence reference, no quotes.</param>
/// <param name="WhatWouldChange">What the agent would change.</param>
/// <param name="Unsure">What the agent is unsure of.</param>
public sealed record VentInput(string WhatHappened, string WhatWouldChange, string Unsure);

/// <summary>The taint facts about the session that wrote the vent (doc 15, C06).</summary>
/// <param name="HardTainted">The session has seen external content.</param>
/// <param name="SourceSoftTainted">The agent text came from a softly tainted source session.</param>
public sealed record VentTaint(bool HardTainted, bool SourceSoftTainted);

/// <summary>A vent: the only text that may leave the agent's private GC is the sanitized fields. Vents are never scored.</summary>
/// <param name="FailureRef">The failed card or review reference. Keys the vent, so a restart writes no second copy.</param>
/// <param name="AgentId">The writing agent.</param>
/// <param name="WhatHappened">Sanitized account.</param>
/// <param name="WhatWouldChange">Sanitized change.</param>
/// <param name="Unsure">Sanitized doubts.</param>
/// <param name="Status">The vent's status.</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="PublishTarget">The target, once published.</param>
public sealed record VentDraft(
    string FailureRef,
    Guid AgentId,
    string WhatHappened,
    string WhatWouldChange,
    string Unsure,
    VentStatus Status,
    DateTimeOffset CreatedAt,
    string? PublishTarget = null);

/// <summary>The result of a vent publication request.</summary>
/// <param name="Draft">The vent after the request.</param>
/// <param name="Refusal">Why it was refused, or null when published.</param>
public sealed record VentPublishResult(VentDraft? Draft, string? Refusal)
{
    /// <summary>True when published.</summary>
    public bool Published => Refusal is null;
}

/// <summary>Posts a published vent to its target (the internal General Bazaar GC, or an operator target from doc 36).</summary>
public interface IVentPublisher
{
    /// <summary>Posts the vent. Throwing leaves the vent unpublished.</summary>
    Task PublishAsync(VentDraft draft, string target, CancellationToken cancellationToken);
}

/// <summary>Vent writing, the card gate and the publish gate (doc 26, section 2).</summary>
public interface IVentService
{
    /// <summary>
    /// Notes that an agent's card failed (returned from Review, owner-rejected, or reverted). While
    /// <c>vent.required_before_card</c> is on, the agent may not claim a new card until it vents this failure.
    /// Idempotent per failure reference.
    /// </summary>
    Task RecordFailureAsync(Guid agentId, string failureRef, CancellationToken cancellationToken);

    /// <summary>Sanitizes and stores the vent for a failure. Returns the existing vent when one is already stored.</summary>
    Task<VentDraft> WriteAsync(string failureRef, Guid agentId, VentInput input, VentTaint taint, SensitiveTerms terms, CancellationToken cancellationToken);

    /// <summary>True when the agent has no failure still waiting for its vent, or the setting is off.</summary>
    Task<bool> MayClaimCardAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>Publishes a vent. It needs the owner's approval, and an external target must be operator-configured.</summary>
    Task<VentPublishResult> PublishAsync(string failureRef, string target, bool ownerApproved, CancellationToken cancellationToken);
}
