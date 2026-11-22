using Bazaar.Sanitization;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// Vent writing and the publish gate (docs/design/26-fun-systems.md, section 2). Sanitizing is the shared sanitizer. A
/// hard-tainted vent is held; a soft-tainted one is held only when its source session was tainted. Publishing always needs
/// the owner (Tier 2). Vents are never scored.
/// </summary>
public sealed class VentService(
    IVentStore store,
    ITextSanitizer sanitizer,
    PsychWardSettings settings,
    IVentPublisher? publisher = null,
    TimeProvider? time = null) : IVentService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public Task RecordFailureAsync(Guid agentId, string failureRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureRef);
        return store.AddOpenFailureAsync(agentId, failureRef, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VentDraft> WriteAsync(
        string failureRef, Guid agentId, VentInput input, VentTaint taint, SensitiveTerms terms, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureRef);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(taint);
        ArgumentNullException.ThrowIfNull(terms);

        var status = taint.HardTainted || taint.SourceSoftTainted ? VentStatus.Held : VentStatus.Draft;
        var candidate = new VentDraft(
            failureRef,
            agentId,
            sanitizer.Sanitize(input.WhatHappened, terms).Text,
            sanitizer.Sanitize(input.WhatWouldChange, terms).Text,
            sanitizer.Sanitize(input.Unsure, terms).Text,
            status,
            _time.GetUtcNow());

        var stored = await store.GetOrAddAsync(candidate, cancellationToken).ConfigureAwait(false);
        await store.ResolveFailureAsync(failureRef, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <inheritdoc />
    public async Task<bool> MayClaimCardAsync(Guid agentId, CancellationToken cancellationToken) =>
        !settings.VentRequiredBeforeCard || !await store.HasOpenFailureAsync(agentId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<VentPublishResult> PublishAsync(string failureRef, string target, bool ownerApproved, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var draft = await store.GetAsync(failureRef, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return new VentPublishResult(null, "There is no vent for that failure.");
        }

        if (draft.Status == VentStatus.Published)
        {
            return new VentPublishResult(draft, "This vent is already published.");
        }

        if (!ownerApproved)
        {
            return new VentPublishResult(draft, "Publishing a vent needs the owner's approval.");
        }

        if (!string.Equals(target, PsychWardSettings.InternalGeneralGc, StringComparison.Ordinal)
            && !settings.VentOperatorTargets.Contains(target))
        {
            return new VentPublishResult(draft, "That target is not configured by the operator.");
        }

        if (publisher is null)
        {
            return new VentPublishResult(draft, "No vent publisher is configured.");
        }

        var published = draft with { Status = VentStatus.Published, PublishTarget = target };
        await publisher.PublishAsync(published, target, cancellationToken).ConfigureAwait(false);
        await store.UpdateAsync(published, cancellationToken).ConfigureAwait(false);
        return new VentPublishResult(published, null);
    }
}
