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
/// Collects peer approvals for account-creation requests (doc 15). One lock serialises all changes, so a duplicate approval, an
/// approval racing a refusal and a late approval are each decided in one order. Every final decision is audited once and, when it
/// needs the owner, handed off once. The audit row is written before the state changes: no audit, no decision.
/// State is in memory; a request does not survive a restart (the hand-off and audit rows do).
/// </summary>
public sealed class AccountApprovalService : IDisposable
{
    private readonly IApproverDirectory _directory;
    private readonly IAuditLog _audit;
    private readonly IOwnerApprovalHandOff _handOff;
    private readonly AccountApprovalOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AccountApprovalService(
        IApproverDirectory directory,
        IAuditLog audit,
        IOwnerApprovalHandOff handOff,
        IOptions<AccountApprovalOptions> options,
        TimeProvider? time = null)
    {
        _directory = directory;
        _audit = audit;
        _handOff = handOff;
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
            if (_entries.TryGetValue(request.RequestId, out var existing))
            {
                if (!string.Equals(existing.Request.RequesterAgentId, request.RequesterAgentId, StringComparison.Ordinal)
                    || !string.Equals(existing.Request.Service, request.Service, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Request id '{request.RequestId}' is already used for a different request.");
                }

                return existing.Decision;
            }

            var entry = new Entry(request);
            await EvaluateAsync(entry, ct).ConfigureAwait(false);
            _entries[request.RequestId] = entry;
            return entry.Decision;
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
            if (!_entries.TryGetValue(vote.RequestId ?? string.Empty, out var entry))
            {
                return new PeerVoteResult(VoteDisposition.UnknownRequest, null);
            }

            if (!string.Equals(vote.Service, entry.Request.Service, StringComparison.Ordinal))
            {
                return new PeerVoteResult(VoteDisposition.BindingMismatch, entry.Decision);
            }

            if (entry.Decision is not Pending)
            {
                return new PeerVoteResult(VoteDisposition.AlreadyDecided, entry.Decision);
            }

            var candidates = await LoadCandidatesAsync(ct).ConfigureAwait(false);
            var eligible = AccountApprovalPolicy.EligibleApprovers(entry.Request, candidates, _options);
            if (!eligible.Contains(vote.ApproverAgentId, StringComparer.Ordinal))
            {
                await AppendVoteAsync(entry, vote, VoteDisposition.Ineligible, ct).ConfigureAwait(false);
                return new PeerVoteResult(VoteDisposition.Ineligible, entry.Decision);
            }

            if (entry.Votes.Any(v => string.Equals(v.ApproverAgentId, vote.ApproverAgentId, StringComparison.Ordinal) && v.Kind == vote.Kind))
            {
                return new PeerVoteResult(VoteDisposition.Duplicate, entry.Decision);
            }

            await AppendVoteAsync(entry, vote, VoteDisposition.Counted, ct).ConfigureAwait(false);
            entry.Votes.Add(vote);
            try
            {
                await EvaluateAsync(entry, ct).ConfigureAwait(false);
            }
            catch
            {
                entry.Votes.Remove(vote);
                throw;
            }

            return new PeerVoteResult(VoteDisposition.Counted, entry.Decision);
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
            if (!_entries.TryGetValue(requestId, out var entry))
            {
                return null;
            }

            if (entry.Decision is Pending)
            {
                await EvaluateAsync(entry, ct).ConfigureAwait(false);
            }

            return entry.Decision;
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
            return _entries.TryGetValue(requestId, out var entry) ? entry.Decision : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Must run under _gate. Audit, then hand-off, then commit: a failure leaves the request as it was.
    private async Task EvaluateAsync(Entry entry, CancellationToken ct)
    {
        var candidates = await LoadCandidatesAsync(ct).ConfigureAwait(false);
        var decision = AccountApprovalPolicy.Decide(entry.Request, entry.Votes, candidates, _options, _time.GetUtcNow());
        if (decision is not Pending)
        {
            await _audit.AppendAsync(DecisionEntry(entry.Request, decision), ct).ConfigureAwait(false);
            if (decision is NeedsOwner owner)
            {
                await _handOff.RequestOwnerApprovalAsync(new OwnerApprovalRequest(owner.RequestId, owner.Rule.ToString(), owner.Evidence), ct)
                    .ConfigureAwait(false);
            }
        }

        entry.Decision = decision;
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

    private Task AppendVoteAsync(Entry entry, PeerVote vote, VoteDisposition disposition, CancellationToken ct) =>
        _audit.AppendAsync(
            new AuditEntry(
                _time.GetUtcNow(),
                vote.ApproverAgentId,
                "account_approval.peer_vote",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["requestId"] = entry.Request.RequestId,
                    ["requester"] = entry.Request.RequesterAgentId,
                    ["service"] = entry.Request.Service,
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

    private sealed class Entry
    {
        public Entry(AccountApprovalRequest request)
        {
            Request = request;
            Decision = new Pending(request.RequestId, 0, 0);
        }

        public AccountApprovalRequest Request { get; }

        public List<PeerVote> Votes { get; } = [];

        public AccountApprovalDecision Decision { get; set; }
    }
}
