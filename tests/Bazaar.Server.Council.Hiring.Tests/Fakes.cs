using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring.Tests;

/// <summary>Records opened motions and returns predictable vote ids. Other operations are not used by the hire adapter.</summary>
internal sealed class FakeCouncilService : ICouncilService
{
    private readonly object _gate = new();

    public List<(string Caller, MotionDraft Draft, Guid VoteId)> Opened { get; } = [];

    public Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct)
    {
        var voteId = Guid.NewGuid();
        lock (_gate)
        {
            Opened.Add((callerId, motion, voteId));
        }

        return Task.FromResult(voteId);
    }

    public Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct) => throw new NotSupportedException();

    public Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct) => throw new NotSupportedException();

    public Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct) => throw new NotSupportedException();

    public Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>An in-memory hire store, one state per template.</summary>
internal sealed class InMemoryHireDecisionStore : IHireDecisionStore
{
    private readonly Dictionary<string, HireVoteState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int SaveCount { get; private set; }

    /// <summary>Stores a state without counting it as a save, so tests can assert on writes made by the code under test.</summary>
    public void Seed(HireVoteState state)
    {
        lock (_gate)
        {
            _states[state.TemplateId] = state;
        }
    }

    public Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_states.TryGetValue(templateId, out var state) ? state : null);
        }
    }

    public Task SaveAsync(HireVoteState state, CancellationToken ct)
    {
        lock (_gate)
        {
            _states[state.TemplateId] = state;
            SaveCount++;
        }

        return Task.CompletedTask;
    }
}

internal static class Samples
{
    public static readonly Guid GcId = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    public static HireRequest Request(string templateId = "talent-analyst-v2", string proposer = "agent-proposer") =>
        new(templateId, GcId, proposer, SubjectAuthorId: "agent-author", Now.AddDays(1));

    /// <summary>Builds the Council outcome the mapper would produce for a decided hire vote.</summary>
    public static MembershipOutcome Outcome(Guid voteId, string templateId, string choice, MotionClass motionClass = MotionClass.Ordinary)
    {
        var counts = new Dictionary<string, int> { [ApprovalChoices.Yes] = 3, [ApprovalChoices.No] = 0, [ApprovalChoices.Abstain] = 0 };
        var motion = new MembershipMotionMetadata(voteId, MembershipMotionKind.Hire, templateId, motionClass, Now);
        var decision = new TallyDecision(TallyDecisionKind.Decided, choice, null, counts);
        return MembershipOutcomeMapper.Map(decision, motion)!;
    }
}
