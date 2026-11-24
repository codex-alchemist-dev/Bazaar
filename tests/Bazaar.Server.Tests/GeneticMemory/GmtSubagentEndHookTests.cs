using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Memory;

namespace Bazaar.Server.Tests.GeneticMemory;

public class GmtSubagentEndHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSource(GmtChildProposals? proposals) : IGmtProposalSource
    {
        public Task<GmtChildProposals?> ReadAsync(SubagentEnded ended, CancellationToken ct) => Task.FromResult(proposals);
    }

    private sealed class NullSink : Bazaar.Server.Memory.Write.IMemoryBatchSink
    {
        public Task<Bazaar.Server.Memory.Write.MemoryCommitResult> CommitAsync(Bazaar.Server.Memory.Write.MemoryBatch batch, CancellationToken ct) =>
            Task.FromResult(Bazaar.Server.Memory.Write.MemoryCommitResult.Success("sha"));
    }

    private static SubagentEnded Ended(TurnEndReason reason = TurnEndReason.FinalMessage) => new(
        new SubagentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, ["gc.read"], "holder", "task", ["gc.read"], "route"),
        new SubagentResult(Guid.NewGuid(), reason, "done", 3));

    private static GmtProposal Lesson(string title, string body = "Back off on retry.", GmtContentClass contentClass = GmtContentClass.Lesson) =>
        new(contentClass, GmtLessonKind.Procedure, title, body);

    private static GmtChildProposals Proposals(params GmtProposal[] items) =>
        new("subagent-1", "agent-new", "trace/abc", false, false, GmtTerms.None, items);

    private static (GmtSubagentEndHook Hook, GeneticMemoryService Service) Rig(GmtChildProposals? proposals)
    {
        var service = new GeneticMemoryService(new InMemoryGmtLedger(), new GmtLessonWriter(new NullSink()), new GmtSecretScanner());
        return (new GmtSubagentEndHook(new FakeSource(proposals), service), service);
    }

    [Fact]
    public async Task A_finished_child_stages_each_proposal_for_review_and_writes_nothing()
    {
        var (hook, service) = Rig(Proposals(Lesson("Retry"), Lesson("Timeouts")));

        await hook.OnSubagentEndedAsync(Ended(), Ct);

        var queue = await service.QueueAsync(GmtReviewer.Owner, Ct);
        Assert.Equal(["Retry", "Timeouts"], queue.Select(c => c.Title));
        Assert.All(queue, c => Assert.Equal("subagent-1", c.ChildAgent));
    }

    [Theory]
    [InlineData(TurnEndReason.LeaseLost)]
    [InlineData(TurnEndReason.GatewayFailed)]
    public async Task An_abandoned_child_stages_nothing(TurnEndReason reason)
    {
        var (hook, service) = Rig(Proposals(Lesson("Retry")));

        await hook.OnSubagentEndedAsync(Ended(reason), Ct);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task No_proposals_stage_nothing()
    {
        var (hook, service) = Rig(null);

        await hook.OnSubagentEndedAsync(Ended(), Ct);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
    }

    [Fact]
    public async Task A_refused_proposal_is_dropped_and_the_others_still_stage()
    {
        var (hook, service) = Rig(Proposals(
            Lesson("Leaky", "key ghp_abcdefghijklmnopqrstuvwxyz0123456789"),
            Lesson("Pref", contentClass: GmtContentClass.Preference),
            Lesson("Good")));

        await hook.OnSubagentEndedAsync(Ended(), Ct);

        Assert.Equal(["Good"], (await service.QueueAsync(GmtReviewer.Owner, Ct)).Select(c => c.Title));
    }

    [Fact]
    public async Task A_tainted_child_stages_candidates_that_stay_tainted()
    {
        var (hook, service) = Rig(Proposals(Lesson("Retry")) with { ChildTainted = true });

        await hook.OnSubagentEndedAsync(Ended(), Ct);

        Assert.True(Assert.Single(await service.QueueAsync(GmtReviewer.Owner, Ct)).Tainted);
    }

    [Fact]
    public async Task A_quarantined_child_stages_nothing()
    {
        var (hook, service) = Rig(Proposals(Lesson("Retry")) with { ChildQuarantined = true });

        await hook.OnSubagentEndedAsync(Ended(), Ct);

        Assert.Empty(await service.QueueAsync(GmtReviewer.Owner, Ct));
    }
}
