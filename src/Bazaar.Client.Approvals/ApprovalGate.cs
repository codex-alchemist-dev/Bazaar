using System.Security.Cryptography;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals;

/// <summary>
/// The phone's approval gate (doc 29 Approvals, doc 15 flow step 4). A decision is signed and sent only when a fresh
/// biometric success was obtained for that exact approval id, content hash and decision. The success becomes a
/// single-use grant with a short expiry; the gate keeps its own record of it and ignores the fields of any grant object it
/// is handed. Failure, cancellation or any mismatch signs and sends nothing.
/// </summary>
/// <remarks>
/// A signed decision is final on this device: the approval id is marked decided, so a second tap, a concurrent tap or a
/// replay is refused. A failed send is not retried, because retrying would be a replay of a signed decision. The server
/// remains the authority and checks the signature, hash and expiry again.
/// </remarks>
public sealed class ApprovalGate
{
    private static readonly TimeSpan _spentRetention = TimeSpan.FromMinutes(1);

    private readonly IBiometricPrompt _biometric;
    private readonly IDeviceApprovalSigner _signer;
    private readonly IApprovalDecisionSender _sender;
    private readonly ApprovalGateOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>Creates a gate. The signer's device id must have the allowed shape.</summary>
    public ApprovalGate(
        IBiometricPrompt biometric,
        IDeviceApprovalSigner signer,
        IApprovalDecisionSender sender,
        ApprovalGateOptions? options = null,
        TimeProvider? clock = null)
    {
        _biometric = biometric ?? throw new ArgumentNullException(nameof(biometric));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _options = options ?? new ApprovalGateOptions();
        _clock = clock ?? TimeProvider.System;
        if (!ApprovalSigningPayload.IsValidDeviceId(_signer.DeviceId))
        {
            throw new ArgumentException("The signer's device id has a disallowed shape.", nameof(signer));
        }
    }

    /// <summary>
    /// Runs one decision end to end: validate, biometric when needed, sign, send. Approve always asks for a fresh
    /// biometric; deny does only when <see cref="ApprovalGateOptions.RequireBiometricForDeny"/> is set.
    /// </summary>
    public async ValueTask<GateResult> DecideAsync(ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        BiometricGrant? grant = null;
        if (NeedsBiometric(request.Kind))
        {
            var granted = await RequestBiometricAsync(request, cancellationToken).ConfigureAwait(false);
            if (granted.Refusal is { } refusal)
            {
                return new GateResult(refusal, null);
            }

            grant = granted.Grant;
        }

        var result = await SubmitAsync(request, grant, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is not null && grant is not null)
        {
            // A refused submit must not leave a live grant behind for a later tap to pick up.
            Discard(grant);
        }

        return result;
    }

    /// <summary>
    /// Shows the biometric prompt for this exact request and, on success, issues a single-use grant. Failure, cancellation
    /// and unavailability return a refusal and issue nothing.
    /// </summary>
    public async ValueTask<GrantResult> RequestBiometricAsync(ApprovalDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var card = request.Card;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (Validate(request, now) is { } invalid)
            {
                return new GrantResult(invalid, null);
            }

            Prune(now);
            if (_entries.TryGetValue(card.ApprovalId, out var existing))
            {
                return new GrantResult(existing.State == EntryState.Spent ? GateRefusal.AlreadyDecided : GateRefusal.AlreadyInProgress, null);
            }

            _entries[card.ApprovalId] = new Entry(EntryState.Prompting, card.ExpiresAt);
        }

        BiometricOutcome outcome;
        try
        {
            var prompt = new BiometricRequest(card.ApprovalId, card.PayloadHash, "Confirm approval", $"{card.AgentId}: {card.Tool}");
            outcome = await _biometric.AuthenticateAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = BiometricOutcome.Cancelled;
        }
#pragma warning disable CA1031 // Any prompt failure means no biometric success; the reason is not trusted detail.
        catch (Exception)
#pragma warning restore CA1031
        {
            outcome = BiometricOutcome.Failed;
        }

        lock (_gate)
        {
            if (outcome != BiometricOutcome.Succeeded)
            {
                _entries.Remove(card.ApprovalId);
                return new GrantResult(
                    outcome switch
                    {
                        BiometricOutcome.Cancelled => GateRefusal.BiometricCancelled,
                        BiometricOutcome.Unavailable => GateRefusal.BiometricUnavailable,
                        _ => GateRefusal.BiometricFailed,
                    },
                    null);
            }

            var now = _clock.GetUtcNow();
            var expires = now + _options.EffectiveBiometricGrace;
            var token = new Guid(RandomNumberGenerator.GetBytes(16));
            var entry = _entries[card.ApprovalId];
            entry.State = EntryState.Granted;
            entry.Token = token;
            entry.ContentHash = card.PayloadHash;
            entry.Kind = request.Kind;
            entry.GrantExpiresAt = expires;
            return new GrantResult(null, new BiometricGrant(token, card.ApprovalId, card.PayloadHash, request.Kind, now, expires));
        }
    }

    /// <summary>
    /// Signs and sends the decision. A decision that needs a biometric must carry the grant from
    /// <see cref="RequestBiometricAsync"/>; the grant is spent by this call, whether or not it succeeds.
    /// </summary>
    public async ValueTask<GateResult> SubmitAsync(ApprovalDecisionRequest request, BiometricGrant? grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var card = request.Card;
        var approve = request.Kind == ApprovalDecisionKind.ApproveOnce;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (Validate(request, now) is { } invalid)
            {
                return Refused(invalid);
            }

            Prune(now);
            if (NeedsBiometric(request.Kind))
            {
                if (grant is null)
                {
                    return Refused(GateRefusal.BiometricRequired);
                }

                if (grant.ApprovalId != card.ApprovalId || grant.Kind != request.Kind
                    || !string.Equals(grant.ContentHash, card.PayloadHash, StringComparison.Ordinal))
                {
                    // Someone else's grant or a changed card. A hash change also voids the live grant for this approval.
                    if (grant.ApprovalId == card.ApprovalId
                        && _entries.TryGetValue(card.ApprovalId, out var voided)
                        && voided.State == EntryState.Granted
                        && voided.Token == grant.Token)
                    {
                        _entries.Remove(card.ApprovalId);
                    }

                    return Refused(GateRefusal.GrantMismatch);
                }

                if (!_entries.TryGetValue(card.ApprovalId, out var entry))
                {
                    return Refused(GateRefusal.GrantMissingOrReused);
                }

                if (entry.State == EntryState.Spent)
                {
                    return Refused(GateRefusal.AlreadyDecided);
                }

                if (entry.State != EntryState.Granted)
                {
                    return Refused(GateRefusal.AlreadyInProgress);
                }

                if (entry.Token != grant.Token || entry.Token == Guid.Empty)
                {
                    return Refused(GateRefusal.GrantMissingOrReused);
                }

                if (entry.Kind != request.Kind || !string.Equals(entry.ContentHash, card.PayloadHash, StringComparison.Ordinal))
                {
                    _entries.Remove(card.ApprovalId);
                    return Refused(GateRefusal.GrantMismatch);
                }

                if (now >= entry.GrantExpiresAt)
                {
                    _entries.Remove(card.ApprovalId);
                    return Refused(GateRefusal.GrantExpired);
                }

                // Single use: the grant is spent here, before anything is signed.
                entry.State = EntryState.Signing;
                entry.Token = Guid.Empty;
            }
            else
            {
                if (_entries.TryGetValue(card.ApprovalId, out var existing))
                {
                    return Refused(existing.State == EntryState.Spent ? GateRefusal.AlreadyDecided : GateRefusal.AlreadyInProgress);
                }

                _entries[card.ApprovalId] = new Entry(EntryState.Signing, card.ExpiresAt);
            }
        }

        ApprovalDecisionDto dto;
        try
        {
            var payload = ApprovalSigningPayload.Build(card.ApprovalId, approve, card.PayloadHash, card.ExpiresAt, _signer.DeviceId);
            var signature = await _signer.SignAsync(payload, cancellationToken).ConfigureAwait(false);
            if (signature is null || signature.Length != ApprovalSigningPayload.SignatureLength)
            {
                throw new InvalidOperationException("The signer returned a signature of the wrong length.");
            }

            dto = new ApprovalDecisionDto(card.ApprovalId, request.Kind, _signer.DeviceId, Convert.ToBase64String(signature), _clock.GetUtcNow());
        }
#pragma warning disable CA1031 // Any signer failure means nothing was signed; the approval stays open for a fresh attempt.
        catch (Exception)
#pragma warning restore CA1031
        {
            lock (_gate)
            {
                _entries.Remove(card.ApprovalId);
            }

            return Refused(GateRefusal.SigningFailed);
        }

        lock (_gate)
        {
            _entries[card.ApprovalId].State = EntryState.Spent;
        }

        try
        {
            await _sender.SendAsync(dto, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed send is reported, never retried: a retry would replay a signed decision.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Refused(GateRefusal.SendFailed);
        }

        return new GateResult(null, dto);
    }

    /// <summary>Withdraws a grant that was issued but will not be used, so the approval can be decided afresh.</summary>
    public void Discard(BiometricGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            if (_entries.TryGetValue(grant.ApprovalId, out var entry) && entry.State == EntryState.Granted && entry.Token == grant.Token)
            {
                _entries.Remove(grant.ApprovalId);
            }
        }
    }

    private bool NeedsBiometric(ApprovalDecisionKind kind) =>
        kind == ApprovalDecisionKind.ApproveOnce || _options.RequireBiometricForDeny;

    private static GateResult Refused(GateRefusal refusal) => new(refusal, null);

    private static GateRefusal? Validate(ApprovalDecisionRequest request, DateTimeOffset now)
    {
        if (request.Source != ApprovalInputSource.CardButton)
        {
            return GateRefusal.NotFromCard;
        }

        if (request.Kind is not (ApprovalDecisionKind.ApproveOnce or ApprovalDecisionKind.Deny))
        {
            return GateRefusal.UnsupportedDecision;
        }

        var card = request.Card;
        if (card is null || !ApprovalSigningPayload.IsValidContentHash(card.PayloadHash))
        {
            return GateRefusal.InvalidCard;
        }

        if (card.Status != ApprovalCardStatus.Pending)
        {
            return GateRefusal.CardNotPending;
        }

        if (now >= card.ExpiresAt)
        {
            return GateRefusal.CardExpired;
        }

        return card.AvailableDecisions.Contains(request.Kind) ? null : GateRefusal.DecisionNotAvailable;
    }

    private void Prune(DateTimeOffset now)
    {
        // Past its expiry a card is refused before the table is read, so its record is no longer needed.
        List<Guid>? stale = null;
        foreach (var (id, entry) in _entries)
        {
            if (entry.State is EntryState.Spent or EntryState.Granted && now >= entry.CardExpiresAt + _spentRetention)
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _entries.Remove(id);
            }
        }
    }

    private enum EntryState
    {
        Prompting,
        Granted,
        Signing,
        Spent,
    }

    private sealed class Entry(EntryState state, DateTimeOffset cardExpiresAt)
    {
        public EntryState State { get; set; } = state;

        public DateTimeOffset CardExpiresAt { get; } = cardExpiresAt;

        public Guid Token { get; set; }

        public string ContentHash { get; set; } = "";

        public ApprovalDecisionKind Kind { get; set; }

        public DateTimeOffset GrantExpiresAt { get; set; }
    }
}
