using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// The default <see cref="IAccountApprovalStore"/>: state lives in this process and is lost on restart. The Postgres module
/// replaces it. One lock makes each write atomic, with the same rules as the durable store.
/// </summary>
public sealed class InMemoryAccountApprovalStore : IAccountApprovalStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<AccountApprovalRecord?> GetAsync(string requestId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_entries.TryGetValue(requestId, out var entry) ? entry.ToRecord() : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(AccountApprovalRequest request, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_entries.TryAdd(request.RequestId, new Entry(request, decision)));
        }
    }

    /// <inheritdoc />
    public Task<StoredVoteOutcome> TryAppendVoteAsync(PeerVote vote, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(vote.RequestId, out var entry))
            {
                return Task.FromResult(StoredVoteOutcome.UnknownRequest);
            }

            if (entry.Decision is not Pending)
            {
                return Task.FromResult(StoredVoteOutcome.AlreadyDecided);
            }

            if (entry.Votes.Any(v => string.Equals(v.ApproverAgentId, vote.ApproverAgentId, StringComparison.Ordinal) && v.Kind == vote.Kind))
            {
                return Task.FromResult(StoredVoteOutcome.DuplicateVote);
            }

            entry.Votes.Add(vote);
            entry.Decision = decision;
            return Task.FromResult(StoredVoteOutcome.Stored);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryDecideAsync(string requestId, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(requestId, out var entry) || entry.Decision is not Pending)
            {
                return Task.FromResult(false);
            }

            entry.Decision = decision;
            return Task.FromResult(true);
        }
    }

    private sealed class Entry(AccountApprovalRequest request, AccountApprovalDecision decision)
    {
        public AccountApprovalRequest Request { get; } = request;

        public List<PeerVote> Votes { get; } = [];

        public AccountApprovalDecision Decision { get; set; } = decision;

        public AccountApprovalRecord ToRecord() => new(Request, [.. Votes], Decision);
    }
}
