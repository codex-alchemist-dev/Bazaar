using Bazaar.Server.Council;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Tests.Council.Service;

/// <summary>Clock the tests move by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory store with version checks. <see cref="ForcedMismatches"/> makes the next transitions lose the race.</summary>
internal sealed class InMemoryCouncilStore : ICouncilStore
{
    private readonly Dictionary<Guid, CouncilVoteRecord> _votes = new();
    private readonly List<StoredBallot> _ballots = new();

    public int ForcedMismatches { get; set; }

    public Task<CouncilVoteRecord?> FindVoteAsync(Guid voteId, CancellationToken ct) =>
        Task.FromResult(_votes.GetValueOrDefault(voteId));

    public Task InsertVoteAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        _votes[vote.VoteId] = vote;
        return Task.CompletedTask;
    }

    public Task<bool> TryTransitionAsync(
        Guid voteId,
        long expectedVersion,
        VoteState target,
        string reason,
        DateTimeOffset at,
        DateTimeOffset? newDeadlineAt,
        int? newExtensions,
        CancellationToken ct)
    {
        if (ForcedMismatches > 0)
        {
            ForcedMismatches--;
            return Task.FromResult(false);
        }

        var vote = _votes[voteId];
        if (vote.Version != expectedVersion)
        {
            return Task.FromResult(false);
        }

        _votes[voteId] = vote with
        {
            State = target,
            Version = vote.Version + 1,
            DeadlineAt = newDeadlineAt ?? vote.DeadlineAt,
            Extensions = newExtensions ?? vote.Extensions,
        };
        return Task.FromResult(true);
    }

    public Task SetVaultDecisionNoAsync(Guid voteId, int decisionNo, CancellationToken ct)
    {
        _votes[voteId] = _votes[voteId] with { VaultDecisionNo = decisionNo };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredBallot>> ListBallotsAsync(Guid voteId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredBallot>>(_ballots.Where(ballot => ballot.VoteId == voteId).ToList());

    public Task<bool> TryInsertBallotAsync(StoredBallot ballot, CancellationToken ct)
    {
        if (_ballots.Any(existing => existing.VoteId == ballot.VoteId
            && existing.VoterId == ballot.VoterId
            && existing.Phase == ballot.Phase))
        {
            return Task.FromResult(false);
        }

        _ballots.Add(ballot);
        return Task.FromResult(true);
    }

    /// <summary>Test helper: sets the state directly, as a crash-recovery test needs.</summary>
    public void SetState(Guid voteId, VoteState state) =>
        _votes[voteId] = _votes[voteId] with { State = state, Version = _votes[voteId].Version + 1 };

    /// <summary>Test helper: writes a ballot without the service, so the vote does not reveal.</summary>
    public void InsertBallotDirectly(StoredBallot ballot) => _ballots.Add(ballot);
}

/// <summary>A GC event as the fake recorded it.</summary>
internal sealed record RecordedGcEvent(Guid GcId, string Kind, string PayloadJson, string IdempotencyKey);

/// <summary>GC event sink that honours idempotency keys: a repeated key appends nothing.</summary>
internal sealed class RecordingGcEvents : ICouncilGcEvents
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public List<RecordedGcEvent> Events { get; } = new();

    public Task AppendAsync(Guid gcId, string kind, string payloadJson, string idempotencyKey, CancellationToken ct)
    {
        if (_keys.Add(idempotencyKey))
        {
            Events.Add(new RecordedGcEvent(gcId, kind, payloadJson, idempotencyKey));
        }

        return Task.CompletedTask;
    }

    public IEnumerable<RecordedGcEvent> OfKind(string kind) => Events.Where(evt => evt.Kind == kind);
}

/// <summary>Membership fake with a settable member list.</summary>
internal sealed class FixedMembership : IGcMembership
{
    public List<GcMember> Members { get; } = new();

    public Task<IReadOnlyList<GcMember>> ListMembersAsync(Guid gcId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GcMember>>(Members.ToList());
}

/// <summary>Vault decision writer fake that numbers decisions from 1.</summary>
internal sealed class RecordingDecisionWriter : ICouncilDecisionWriter
{
    public List<(Guid VoteId, string Slug, string Markdown)> Writes { get; } = new();

    public Task<int> WriteDecisionAsync(Guid voteId, string slug, string markdownBody, CancellationToken ct)
    {
        Writes.Add((voteId, slug, markdownBody));
        return Task.FromResult(Writes.Count);
    }
}

/// <summary>Wires the fakes together and builds a service over them.</summary>
internal sealed class CouncilHarness
{
    public Guid GcId { get; } = Guid.NewGuid();

    public ManualClock Clock { get; } = new();

    public InMemoryCouncilStore Store { get; } = new();

    public RecordingGcEvents Events { get; } = new();

    public FixedMembership Membership { get; } = new();

    public RecordingDecisionWriter Writer { get; } = new();

    public CouncilOptions Options { get; set; } = new();

    public bool WithWriter { get; set; } = true;

    public CouncilService CreateService() =>
        new(Store, Events, Membership, Options, Clock, WithWriter ? Writer : null);

    /// <summary>Adds agent members and, optionally, the owner.</summary>
    public void AddAgents(params string[] agents)
    {
        foreach (var agent in agents)
        {
            Membership.Members.Add(new GcMember(agent, GcMemberKind.Agent));
        }
    }

    public void AddOwner() => Membership.Members.Add(new GcMember("owner", GcMemberKind.Owner));

    public MotionDraft Motion(
        VoteKind kind = VoteKind.ApproveReject,
        MotionClass motionClass = MotionClass.Ordinary,
        IReadOnlyList<string>? options = null,
        string proposer = "ada",
        string? author = null,
        string? membershipSubject = null,
        DateTimeOffset? deadline = null) =>
        new(
            SubjectRef: "task/42",
            Kind: kind,
            MotionClass: motionClass,
            Options: options ?? Array.Empty<string>(),
            GcId: GcId,
            ProposerId: proposer,
            SubjectAuthorId: author,
            Deadline: deadline ?? Clock.Now.AddHours(100),
            MembershipSubjectId: membershipSubject);

    /// <summary>The standard three-voter electorate: ada proposes, cal, dot and eve vote.</summary>
    public void AddThreeVoters()
    {
        AddAgents("ada", "cal", "dot", "eve");
        AddOwner();
    }
}

internal static class Ballots
{
    public const string Reasoning = "The evidence points this way. The cost is acceptable.";

    public static Ballot Independent(string voter, string choice) =>
        new(voter, choice, Reasoning, BallotPhase.Independent);

    public static Ballot FollowUp(string voter, string choice) =>
        new(voter, choice, "After reading the reasons I changed. The argument holds.", BallotPhase.FollowUp);
}
