using System.Collections.Concurrent;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AccountApproval.Tests;

internal sealed class FakeDirectory : IApproverDirectory
{
    public ConcurrentDictionary<string, ApproverStanding> Standings { get; } = new(StringComparer.Ordinal);

    public bool Throw { get; set; }

    public Task<ApproverStanding?> GetStandingAsync(string agentId, CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("directory down");
        }

        return Task.FromResult(Standings.TryGetValue(agentId, out var s) ? s : null);
    }
}

internal sealed class RecordingAudit : IAuditLog
{
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();

    public bool Throw { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("audit down");
        }

        Entries.Enqueue(e);
        return Task.CompletedTask;
    }

    public AuditEntry[] OfKind(string kind) => Entries.Where(e => e.Kind == kind).ToArray();
}

internal sealed class RecordingHandOff : IOwnerApprovalHandOff
{
    public ConcurrentQueue<OwnerApprovalRequest> Requests { get; } = new();

    public Task RequestOwnerApprovalAsync(OwnerApprovalRequest request, CancellationToken ct = default)
    {
        Requests.Enqueue(request);
        return Task.CompletedTask;
    }
}

internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public static ApproverStanding Good { get; } = new(true, true, true);

    public static AccountApprovalOptions Options(int quorum = 2, bool agentAcceptsTerms = false, params string[] approvers) =>
        new()
        {
            PeerQuorum = quorum,
            AgentAcceptsTerms = agentAcceptsTerms,
            PeerApprovers = approvers.Length == 0 ? ["agent-a", "agent-b", "agent-c"] : [.. approvers],
        };

    public static AccountApprovalRequest Request(
        string id = "req-1",
        string requester = "agent-r",
        string service = "example.test",
        RequestedAction action = RequestedAction.CreateAccount,
        ServiceTermsStatus terms = ServiceTermsStatus.Verified,
        bool whitelisted = true,
        bool spend = false,
        bool otherPerson = false,
        PersonStep steps = PersonStep.None) =>
        new(id, requester, service, action, terms, "https://example.test/terms", "v1", whitelisted, spend, otherPerson, steps, Now);

    public static PeerVote Vote(string approver, PeerVoteKind kind = PeerVoteKind.Approve, string id = "req-1", string service = "example.test") =>
        new(id, service, approver, kind, "model-x", "hash-1", Now);

    public static ApproverCandidate[] AllGood(params string[] ids) =>
        [.. (ids.Length == 0 ? ["agent-a", "agent-b", "agent-c"] : ids).Select(i => new ApproverCandidate(i, Good))];

    public static IOptions<AccountApprovalOptions> Wrap(AccountApprovalOptions o) => Microsoft.Extensions.Options.Options.Create(o);
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Current { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Current;
}

internal sealed class Harness
{
    public FakeDirectory Directory { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public RecordingHandOff HandOff { get; } = new();

    public FixedTime Time { get; } = new(Fixtures.Now);

    public IAccountApprovalStore Store { get; }

    public AccountApprovalOptions Options { get; }

    public AccountApprovalService Service { get; }

    public Harness(AccountApprovalOptions? options = null, bool allGood = true, IAccountApprovalStore? store = null)
    {
        options ??= Fixtures.Options();
        Options = options;
        Store = store ?? new InMemoryAccountApprovalStore();
        if (allGood)
        {
            foreach (var id in options.PeerApprovers)
            {
                Directory.Standings[id] = Fixtures.Good;
            }
        }

        Service = new AccountApprovalService(Directory, Audit, HandOff, Store, Fixtures.Wrap(options), Time);
    }

    /// <summary>A second service over the same store, as after a server restart: the new instance holds no state of its own.</summary>
    public AccountApprovalService Restart() =>
        new(Directory, Audit, HandOff, Store, Fixtures.Wrap(Options), Time);
}
