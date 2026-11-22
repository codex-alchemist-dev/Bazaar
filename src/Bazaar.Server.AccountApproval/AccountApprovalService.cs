using System.Text.Json;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AccountApproval;

/// <summary>What happened to one submitted peer vote.</summary>
public enum VoteDisposition
{
    /// <summary>Recorded and counted.</summary>
    Counted,

    /// <summary>The same approver already gave the same answer. Counted once, nothing recorded again.</summary>
    Duplicate,

    /// <summary>The voter is not an eligible senior peer for this request. Ignored; it cannot refuse or approve.</summary>
    Ineligible,

    /// <summary>No such request.</summary>
    UnknownRequest,

    /// <summary>The vote names a different service than the request. Rejected.</summary>
    BindingMismatch,

    /// <summary>The request already reached a final decision. The vote changes nothing (an approval never resurrects a refused request).</summary>
    AlreadyDecided,
}

public sealed record PeerVoteResult(VoteDisposition Disposition, AccountApprovalDecision? Decision);

/// <summary>
/// Collects peer approvals for account-creation requests (doc 15). One lock serialises all changes in this process, and the store
/// applies each vote and decision atomically and only while the request is pending, so a duplicate approval, an approval racing a
/// refusal and a late approval are each decided in one order. Every final decision is audited once and, when it needs the owner,
/// handed off once. The audit row is written before the state changes: no audit, no decision. Any store error propagates: the request
/// is not approved (fail closed). State lives in the <see cref="IAccountApprovalStore"/>: in memory by default, in Postgres when
/// <c>Bazaar.Server.AccountApproval.Postgres</c> is loaded, in which case requests and votes survive a restart.
/// </summary>
public sealed class AccountApprovalService : IDisposable
{
    private readonly IApproverDirectory _directory;
    private readonly IAuditLog _audit;
    private readonly IOwnerApprovalHandOff _handOff;
    private readonly IAccountApprovalStore _store;
    private readonly AccountApprovalOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountApprovalService(
        IApproverDirectory directory,
        IAuditLog audit,
        IOwnerApprovalHandOff handOff,
        IAccountApprovalStore store,
        IOptions<AccountApprovalOptions> options,
        TimeProvider? time = null)
    {
        _directory = directory;
        _audit = audit;
        _handOff = handOff;
        _store = store;
        _options = options.Value;
        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(nameof(AccountApprovalOptions), typeof(AccountApprovalOptions), errors);
        }

        _time = time ?? TimeProvider.System;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Starts a request. Submitting the same request id again returns the current decision.</summary>
    public async Task<AccountApprovalDecision> SubmitAsync(AccountApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequesterAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Service);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _store.GetAsync(request.RequestId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return SameRequestOrThrow(existing, request).Decision;
            }

            var decision = await EvaluateAsync(request, [], ct).ConfigureAwait(false);
            if (!await _store.TryCreateAsync(request, decision, ct).ConfigureAwait(false))
            {
                // Another writer stored this id first. Its decision stands.
                var winner = await _store.GetAsync(request.RequestId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Request id '{request.RequestId}' could not be stored.");
                return SameRequestOrThrow(winner, request).Decision;
            }

            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records one senior peer's approval or refusal.</summary>
    public async Task<PeerVoteResult> RecordVoteAsync(PeerVote vote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = await _store.GetAsync(vote.RequestId ?? string.Empty, ct).ConfigureAwait(false);
            if (record is null)
            {
                return new PeerVoteResult(VoteDisposition.UnknownRequest, null);
            }

            if (!string.Equals(vote.Service, record.Request.Service, StringComparison.Ordinal))
            {
                return new PeerVoteResult(VoteDisposition.BindingMismatch, record.Decision);
            }

            if (record.Decision is not Pending)
            {
                return new PeerVoteResult(VoteDisposition.AlreadyDecided, record.Decision);
            }

            var candidates = await LoadCandidatesAsync(ct).ConfigureAwait(false);
            var eligible = AccountApprovalPolicy.EligibleApprovers(record.Request, candidates, _options);
            if (!eligible.Contains(vote.ApproverAgentId, StringComparer.Ordinal))
            {
                await AppendVoteAsync(record.Request, vote, VoteDisposition.Ineligible, ct).ConfigureAwait(false);
                return new PeerVoteResult(VoteDisposition.Ineligible, record.Decision);
            }

            if (record.Votes.Any(v => string.Equals(v.ApproverAgentId, vote.ApproverAgentId, StringComparison.Ordinal) && v.Kind == vote.Kind))
            {
                return new PeerVoteResult(VoteDisposition.Duplicate, record.Decision);
            }

            await AppendVoteAsync(record.Request, vote, VoteDisposition.Counted, ct).ConfigureAwait(false);
            var decision = await EvaluateAsync(record.Request, [.. record.Votes, vote], ct).ConfigureAwait(false);
            switch (await _store.TryAppendVoteAsync(vote, decision, ct).ConfigureAwait(false))
            {
                case StoredVoteOutcome.Stored:
                    return new PeerVoteResult(VoteDisposition.Counted, decision);
                case StoredVoteOutcome.DuplicateVote:
                    return new PeerVoteResult(VoteDisposition.Duplicate, record.Decision);
                case StoredVoteOutcome.AlreadyDecided:
                    var current = await _store.GetAsync(record.Request.RequestId, ct).ConfigureAwait(false);
                    return new PeerVoteResult(VoteDisposition.AlreadyDecided, current?.Decision ?? record.Decision);
                default:
                    return new PeerVoteResult(VoteDisposition.UnknownRequest, null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-checks a pending request against the clock and the roster (expiry, a peer who fell asleep or was tainted).</summary>
    public async Task<AccountApprovalDecision?> ReevaluateAsync(string requestId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = await _store.GetAsync(requestId, ct).ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            if (record.Decision is not Pending)
            {
                return record.Decision;
            }

            var decision = await EvaluateAsync(record.Request, record.Votes, ct).ConfigureAwait(false);
            if (decision == record.Decision)
            {
                return decision; // Still pending with the same count: nothing new to record.
            }

            if (!await _store.TryDecideAsync(requestId, decision, ct).ConfigureAwait(false))
            {
                return (await _store.GetAsync(requestId, ct).ConfigureAwait(false))?.Decision;
            }

            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountApprovalDecision?> GetAsync(string requestId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await _store.GetAsync(requestId, ct).ConfigureAwait(false))?.Decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AccountApprovalRecord SameRequestOrThrow(AccountApprovalRecord existing, AccountApprovalRequest request) =>
        string.Equals(existing.Request.RequesterAgentId, request.RequesterAgentId, StringComparison.Ordinal)
        && string.Equals(existing.Request.Service, request.Service, StringComparison.Ordinal)
            ? existing
            : throw new InvalidOperationException($"Request id '{request.RequestId}' is already used for a different request.");

    // Must run under _gate. Audit, then hand-off; the caller commits the decision to the store afterwards: a failure leaves the request as it was.
    private async Task<AccountApprovalDecision> EvaluateAsync(
        AccountApprovalRequest request, IReadOnlyList<PeerVote> votes, CancellationToken ct)
    {
        var candidates = await LoadCandidatesAsync(ct).ConfigureAwait(false);
        var decision = AccountApprovalPolicy.Decide(request, votes, candidates, _options, _time.GetUtcNow());
        if (decision is not Pending)
        {
            await _audit.AppendAsync(DecisionEntry(request, decision), ct).ConfigureAwait(false);
            if (decision is NeedsOwner owner)
            {
                await _handOff.RequestOwnerApprovalAsync(new OwnerApprovalRequest(owner.RequestId, owner.Rule.ToString(), owner.Evidence), ct)
                    .ConfigureAwait(false);
            }
        }

        return decision;
    }

    private async Task<IReadOnlyList<ApproverCandidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        var result = new List<ApproverCandidate>();
        foreach (var id in _options.PeerApprovers.Distinct(StringComparer.Ordinal))
        {
            ApproverStanding? standing;
            try
            {
                standing = await _directory.GetStandingAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                standing = null; // Fail closed: an agent the directory cannot vouch for is not eligible.
            }

            result.Add(new ApproverCandidate(id, standing ?? ApproverStanding.None));
        }

        return result;
    }

    private Task AppendVoteAsync(AccountApprovalRequest request, PeerVote vote, VoteDisposition disposition, CancellationToken ct) =>
        _audit.AppendAsync(
            new AuditEntry(
                _time.GetUtcNow(),
                vote.ApproverAgentId,
                "account_approval.peer_vote",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["requester"] = request.RequesterAgentId,
                    ["service"] = request.Service,
                    ["approver"] = vote.ApproverAgentId,
                    ["vote"] = vote.Kind.ToString(),
                    ["disposition"] = disposition.ToString(),
                    ["model"] = vote.ModelId,
                    ["reasoningHash"] = vote.ReasoningHash,
                })),
            ct);

    private AuditEntry DecisionEntry(AccountApprovalRequest request, AccountApprovalDecision decision)
    {
        var votes = decision switch
        {
            ApprovedByPeers approved => approved.Approvals,
            NeedsOwner owner => owner.Evidence.PeerVotes,
            _ => [],
        };
        return new AuditEntry(
            _time.GetUtcNow(),
            request.RequesterAgentId,
            "account_approval.decision",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId,
                ["requester"] = request.RequesterAgentId,
                ["service"] = request.Service,
                ["outcome"] = decision.GetType().Name,
                ["rule"] = decision.Rule.ToString(),
                ["approvers"] = votes.Where(v => v.Kind == PeerVoteKind.Approve).Select(v => v.ApproverAgentId).ToArray(),
                ["refusers"] = votes.Where(v => v.Kind == PeerVoteKind.Refuse).Select(v => v.ApproverAgentId).ToArray(),
                ["siteVoteReference"] = request.SiteVote?.VoteReference,
            }));
    }
}
