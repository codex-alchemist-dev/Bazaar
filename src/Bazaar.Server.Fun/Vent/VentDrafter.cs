using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Fun.Vent;

/// <summary>The taint facts the drafter needs about the session that wrote the vent (doc 15; C06).</summary>
/// <param name="HardTainted">The session has seen external content (hard taint).</param>
/// <param name="SourceSoftTainted">The agent text came from a softly tainted source session.</param>
public sealed record VentTaint(bool HardTainted, bool SourceSoftTainted);

/// <summary>The raw fields of a vent before sanitizing.</summary>
/// <param name="WhatHappened">What happened, by GC sequence reference.</param>
/// <param name="WhatWouldChange">What the agent would change.</param>
/// <param name="Unsure">What the agent is unsure of.</param>
public sealed record VentInput(string WhatHappened, string WhatWouldChange, string Unsure);

/// <summary>Builds sanitized vent drafts and enforces the publish gate (doc 26, section 2).</summary>
public sealed class VentDrafter
{
    private readonly ITextSanitizer _sanitizer;

    /// <summary>Creates the drafter.</summary>
    /// <param name="sanitizer">The shared Psych Ward sanitizer.</param>
    public VentDrafter(ITextSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);

        _sanitizer = sanitizer;
    }

    /// <summary>
    /// Sanitizes the fields and sets the status. A hard-tainted draft is held for the owner. A soft-tainted draft is held
    /// only when its source session was tainted (C06). Vents are never scored.
    /// </summary>
    /// <param name="failureRef">The failure reference the draft is keyed by.</param>
    /// <param name="agentId">The writing agent.</param>
    /// <param name="input">The raw fields.</param>
    /// <param name="taint">The taint facts for the session.</param>
    /// <param name="terms">Names and passages to remove.</param>
    public VentDraft Draft(string failureRef, string agentId, VentInput input, VentTaint taint, SensitiveTerms terms)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureRef);
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(taint);
        ArgumentNullException.ThrowIfNull(terms);

        var status = taint.HardTainted || taint.SourceSoftTainted ? VentStatus.Held : VentStatus.Draft;

        return new VentDraft(
            failureRef,
            agentId,
            _sanitizer.Sanitize(input.WhatHappened, terms).Text,
            _sanitizer.Sanitize(input.WhatWouldChange, terms).Text,
            _sanitizer.Sanitize(input.Unsure, terms).Text,
            status);
    }

    /// <summary>
    /// Checks whether a vent may be published. Publishing is a Tier 2 action (doc 15), so it always needs owner approval.
    /// A held draft is released only by that approval. An external target must be one the operator configured.
    /// </summary>
    /// <param name="draft">The draft to publish.</param>
    /// <param name="target">The requested target.</param>
    /// <param name="settings">The vent settings with the operator's targets.</param>
    /// <param name="ownerApproved">True when the owner approved this publication.</param>
    /// <returns>The published draft, or the refusal reason.</returns>
    public static (VentDraft? Draft, string? Refusal) Publish(VentDraft draft, string target, VentSettings settings, bool ownerApproved)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(settings);

        if (draft.Status == VentStatus.Published)
        {
            return (null, "This vent is already published.");
        }

        if (!ownerApproved)
        {
            return (null, "Publishing a vent needs the owner's approval.");
        }

        var internalTarget = string.Equals(target, VentSettings.InternalGeneralGc, StringComparison.Ordinal);
        if (!internalTarget && !settings.OperatorTargets.Contains(target))
        {
            return (null, "That target is not configured by the operator.");
        }

        return (draft with { Status = VentStatus.Published, PublishTarget = target }, null);
    }
}
