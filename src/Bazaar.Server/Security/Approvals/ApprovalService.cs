using System.Collections.Immutable;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// Owner approvals for tool calls (doc 15, "Approval flow"). Requests a decision, verifies the owner's signature,
/// hands the policy engine a grant that covers exactly one call, and consumes the grant when that call runs.
/// </summary>
/// <remarks>
/// Invariants: a decision is bound to the stored dispatch hash, so changed arguments void it; an expired request
/// denies; a double approve is idempotent; a grant is single-use. Agent text never reaches this service as authority:
/// only a signed decision moves a request.
/// </remarks>
public sealed class ApprovalService(
    IApprovalStore store,
    IOwnerKeyRegistry keys,
    IRateBucketStore rateBuckets,
    ApprovalOptions options)
{
    private static readonly TimeSpan _requestWindow = TimeSpan.FromHours(1);

    private readonly IApprovalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IOwnerKeyRegistry _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly IRateBucketStore _rateBuckets = rateBuckets ?? throw new ArgumentNullException(nameof(rateBuckets));
    private readonly ApprovalOptions _options = ValidOptions(options);

    /// <summary>Outcome of <see cref="RequestAsync"/>.</summary>
    public enum RequestOutcome
    {
        /// <summary>A pending request was stored.</summary>
        Created,

        /// <summary>The agent has used its hourly request cap.</summary>
        RateLimited,

        /// <summary>The arguments cannot be hashed in canonical form.</summary>
        MalformedArguments,
    }

    /// <summary>Result of <see cref="RequestAsync"/>.</summary>
    public sealed record RequestResult(RequestOutcome Outcome, ApprovalRequest? Request);

    /// <summary>Outcome of <see cref="DecideAsync"/>.</summary>
    public enum DecisionOutcome
    {
        /// <summary>The decision was recorded.</summary>
        Recorded,

        /// <summary>The same decision was already recorded. Nothing changed.</summary>
        AlreadyDecided,

        /// <summary>The request expired before the decision arrived. Nothing can be approved now.</summary>
        Expired,

        /// <summary>The device is unknown or revoked, or the signature does not verify. Nothing changed.</summary>
        InvalidSignature,

        /// <summary>The device id has a disallowed shape. Nothing changed.</summary>
        InvalidDeviceId,

        /// <summary>No such approval.</summary>
        NotFound,

        /// <summary>The request already has the opposite decision or is past its decision.</summary>
        Conflict,
    }

    /// <summary>Result of <see cref="DecideAsync"/>.</summary>
    public sealed record DecisionResult(DecisionOutcome Outcome, ApprovalRequest? Request);

    /// <summary>
    /// Stores a pending request for a call that needs the owner. Uses the agent's hourly request cap first, so a
    /// runaway agent cannot fill the owner's card queue.
    /// </summary>
    public async Task<RequestResult> RequestAsync(DispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] hash;
        try
        {
            hash = DispatchHash.Compute(context);
        }
        catch (ArgumentException)
        {
            return new RequestResult(RequestOutcome.MalformedArguments, null);
        }

        var cap = new RateRule(_options.RequestCapPerAgentHour, _requestWindow);
        if (!_rateBuckets.TryConsume($"approval-request|{context.AgentId}", cap, context.Now).Allowed)
        {
            return new RequestResult(RequestOutcome.RateLimited, null);
        }

        var request = new ApprovalRequest(
            Guid.CreateVersion7(context.Now),
            context.AgentId,
            context.SessionId,
            context.Tool,
            context.Target,
            hash,
            context.Args.GetRawText(),
            context.Taint.Sources,
            ApprovalStatus.Pending,
            context.Now,
            context.Now + TimeSpan.FromHours(_options.ExpiryHours));

        await _store.InsertAsync(request, ct).ConfigureAwait(false);
        return new RequestResult(RequestOutcome.Created, request);
    }

    /// <summary>
    /// Records the owner's signed decision. The signature must verify over the stored hash, expiry and device, so the
    /// client cannot change what was approved. Repeating the same decision is idempotent; expiry is applied first.
    /// </summary>
    public async Task<DecisionResult> DecideAsync(
        Guid id,
        ApprovalDecision decision,
        string deviceId,
        byte[] signature,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (!ApprovalSignature.IsValidDeviceId(deviceId))
        {
            return new DecisionResult(DecisionOutcome.InvalidDeviceId, null);
        }

        var request = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (request is null)
        {
            return new DecisionResult(DecisionOutcome.NotFound, null);
        }

        if (request.Status == ApprovalStatus.Pending && now >= request.ExpiresAt)
        {
            var expired = request with { Status = ApprovalStatus.Expired };
            await _store.TryReplaceAsync(id, ApprovalStatus.Pending, expired, ct).ConfigureAwait(false);
            return new DecisionResult(DecisionOutcome.Expired, expired);
        }

        var key = await _keys.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (key is null || key.Revoked)
        {
            return new DecisionResult(DecisionOutcome.InvalidSignature, request);
        }

        var message = ApprovalSignature.Message(id, decision, request.ArgsHash, request.ExpiresAt, deviceId);
        if (!ApprovalSignature.Verify(key.PublicKeySpki, message, signature))
        {
            return new DecisionResult(DecisionOutcome.InvalidSignature, request);
        }

        var target = decision == ApprovalDecision.Approve ? ApprovalStatus.Approved : ApprovalStatus.Denied;
        if (request.Status == ApprovalStatus.Pending)
        {
            var updated = request with
            {
                Status = target,
                DecidedBy = key.OwnerId,
                DeviceId = deviceId,
                DecidedAt = now,
                Signature = signature,
            };

            if (await _store.TryReplaceAsync(id, ApprovalStatus.Pending, updated, ct).ConfigureAwait(false))
            {
                return new DecisionResult(DecisionOutcome.Recorded, updated);
            }

            // Lost a race. Re-read and judge the row that won.
            request = await _store.GetAsync(id, ct).ConfigureAwait(false) ?? request;
        }

        return IsSameDecision(request.Status, target)
            ? new DecisionResult(DecisionOutcome.AlreadyDecided, request)
            : new DecisionResult(DecisionOutcome.Conflict, request);
    }

    /// <summary>
    /// Returns a grant for a dispatch if an approved, unexpired, unconsumed request matches its agent, tool and hash.
    /// Returns null for every other case, with no error to leak detail to the caller.
    /// </summary>
    public async Task<VerifiedApproval?> VerifyGrantAsync(Guid approvalId, DispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = await _store.GetAsync(approvalId, ct).ConfigureAwait(false);
        if (request is null || request.Status != ApprovalStatus.Approved || context.Now >= request.ExpiresAt)
        {
            return null;
        }

        if (request.AgentId != context.AgentId || request.Tool != context.Tool)
        {
            return null;
        }

        byte[] hash;
        try
        {
            hash = DispatchHash.Compute(context);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!request.ArgsHash.AsSpan().SequenceEqual(hash))
        {
            return null;
        }

        return new VerifiedApproval(request.Id, request.AgentId, request.Tool, request.ArgsHash, request.ExpiresAt);
    }

    /// <summary>
    /// Marks an approved grant as used, after its call has run. Single-use: a second consume returns false.
    /// </summary>
    public async Task<bool> ConsumeAsync(Guid approvalId, DateTimeOffset now, CancellationToken ct = default)
    {
        var request = await _store.GetAsync(approvalId, ct).ConfigureAwait(false);
        if (request is null || request.Status != ApprovalStatus.Approved || now >= request.ExpiresAt)
        {
            return false;
        }

        var consumed = request with { Status = ApprovalStatus.Consumed, ConsumedAt = now };
        return await _store.TryReplaceAsync(approvalId, ApprovalStatus.Approved, consumed, ct).ConfigureAwait(false);
    }

    private static bool IsSameDecision(ApprovalStatus current, ApprovalStatus target) =>
        target == ApprovalStatus.Approved
            ? current is ApprovalStatus.Approved or ApprovalStatus.Consumed
            : current == target;

    private static ApprovalOptions ValidOptions(ApprovalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }
}
