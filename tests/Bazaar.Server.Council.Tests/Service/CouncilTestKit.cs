using Bazaar.Server.Council;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Tests.Service;

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
    private readonly Dictionary<Guid, List<CouncilTransitionRecord>> _transitions = new();

    public int ForcedMismatches { get; set; }

    public Task<CouncilVoteRecord?> FindVoteAsync(Guid voteId, CancellationToken ct) =>
        Task.FromResult(_votes.GetValueOrDefault(voteId));

    public Task InsertVoteAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        _votes[vote.VoteId] = vote;
        _transitions[vote.VoteId] = [new CouncilTransitionRecord(1, VoteState.Collecting, "open", vote.CreatedAt)];
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
        _transitions[voteId].Add(new CouncilTransitionRecord(vote.Version + 1, target, reason, at));
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

    public Task<IReadOnlyList<Guid>> ListVotesDueAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(_votes.Values
            .Where(vote => vote.State == VoteState.Revealed
                || (vote.State is VoteState.Collecting or VoteState.FollowUp && vote.DeadlineAt <= now))
            .OrderByDescending(vote => vote.State == VoteState.Revealed)
            .ThenBy(vote => vote.DeadlineAt)
            .ThenBy(vote => vote.VoteId)
            .Take(limit)
            .Select(vote => vote.VoteId)
            .ToList());

    public Task<IReadOnlyList<Guid>> ListDecidedSinceAsync(DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(_transitions
            .Where(pair => pair.Value.Any(t => t.To == VoteState.Decided && t.At >= since))
            .Select(pair => (Id: pair.Key, At: pair.Value.Single(t => t.To == VoteState.Decided).At))
            .OrderBy(pair => pair.At)
            .ThenBy(pair => pair.Id)
            .Take(limit)
            .Select(pair => pair.Id)
            .ToList());

    public Task<IReadOnlyList<Guid>> ListUndeliveredMembershipDecisionsAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(_votes.Values
            .Where(vote => vote.State == VoteState.Decided
                && vote.MembershipKind is not null
                && vote.MembershipOutcomeDeliveredAt is null)
            .OrderBy(vote => vote.VoteId)
            .Take(limit)
            .Select(vote => vote.VoteId)
            .ToList());

    public Task MarkMembershipOutcomeDeliveredAsync(Guid voteId, DateTimeOffset at, CancellationToken ct)
    {
        var vote = _votes[voteId];
        _votes[voteId] = vote with { MembershipOutcomeDeliveredAt = vote.MembershipOutcomeDeliveredAt ?? at };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CouncilTransitionRecord>> ListTransitionsAsync(Guid voteId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CouncilTransitionRecord>>(_transitions.GetValueOrDefault(voteId)?.ToList()
            ?? new List<CouncilTransitionRecord>());

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

/// <summary>GC event sink that fails the first <see cref="FailKind"/> append, as a crash between two writes would.</summary>
internal sealed class CrashingGcEvents(RecordingGcEvents inner, string failKind) : ICouncilGcEvents
{
    public bool Armed { get; set; } = true;

    public Task AppendAsync(Guid gcId, string kind, string payloadJson, string idempotencyKey, CancellationToken ct)
    {
        if (Armed && kind == failKind)
        {
            throw new IOException("Simulated crash before the event was written.");
        }

        return inner.AppendAsync(gcId, kind, payloadJson, idempotencyKey, ct);
    }
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

/// <summary>Dissent-share source fake that serves one value, or the options default when none is set.</summary>
internal sealed class FixedDissentShareSettings : ICouncilDissentShareSource
{
    public decimal? Share { get; set; }

    public Task<decimal> GetFollowupDissentShareAsync(CancellationToken ct) =>
        Task.FromResult(Share ?? new CouncilOptions().FollowupDissentShare);
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

    public ICouncilDissentShareSource? Settings { get; set; }

    public List<IMembershipOutcomeSink> Sinks { get; } = new();

    public CouncilService CreateService() =>
        new(Store, Events, Membership, Options, Clock, WithWriter ? Writer : null, Settings, Sinks);

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
        DateTimeOffset? deadline = null,
        bool highImpact = false,
        MembershipMotionKind? membershipKind = null) =>
        new(
            SubjectRef: "task/42",
            Kind: kind,
            MotionClass: motionClass,
            Options: options ?? Array.Empty<string>(),
            GcId: GcId,
            ProposerId: proposer,
            SubjectAuthorId: author,
            Deadline: deadline ?? Clock.Now.AddHours(100),
            MembershipSubjectId: membershipSubject,
            HighImpact: highImpact,
            MembershipKind: membershipKind ?? (membershipSubject is null ? null : MembershipMotionKind.Hire));

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
