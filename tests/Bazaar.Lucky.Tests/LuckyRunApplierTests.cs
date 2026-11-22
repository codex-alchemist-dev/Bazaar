using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Sanitization;

namespace Bazaar.Lucky.Tests;

public class LuckyRunApplierTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);
    private static readonly Guid _project = Guid.Parse("00000000-0000-0000-0000-00000000f00d");
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly LuckyCandidate[] _candidates =
    [
        new("agent-a", OnShift: true, OptedIn: false),
        new("agent-b", OnShift: false, OptedIn: true),
        new("agent-c", OnShift: true, OptedIn: true),
    ];

    private sealed class FakeSink : ILuckyRunSink
    {
        public List<string> Calls { get; } = [];
        public Guid GcId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        public List<LuckyGcEntry> Appended { get; } = [];
        public List<LuckyWake> Woken { get; } = [];
        public List<(Guid Gc, Guid Message, string Title)> Cards { get; } = [];
        public long OpeningSeq { get; init; } = 41;

        public Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, Guid ownerId, CancellationToken ct)
        {
            Calls.Add("open");
            return Task.FromResult(new LuckyGcOpened(GcId, OpeningSeq));
        }

        public Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct)
        {
            Calls.Add("append");
            Appended.AddRange(entries);
            IReadOnlyList<LuckyStoredEntry> stored = entries
                .Select(e => new LuckyStoredEntry(e.IdeaIndex, Guid.Parse($"00000000-0000-0000-0000-{e.IdeaIndex + 100:000000000000}")))
                .ToList();
            return Task.FromResult(stored);
        }

        public Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct)
        {
            Calls.Add("wake");
            Woken.AddRange(wakes);
            return Task.FromResult(wakes.Count);
        }

        public Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct)
        {
            Calls.Add("card");
            Cards.Add((gcId, messageId, title));
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private static LuckyRunPlan PlanWithIdeas()
    {
        var controller = new LuckyController(new LuckySettings(), new TextSanitizer());
        var run = controller.Start("run-1", "stuck on the cache", true, _start, 0, _candidates, () => 7).Run!;
        run = controller.RecordIdea(run, "agent-b", "cache per branch", _start.AddMinutes(1), _terms).Run!;
        run = controller.RecordIdea(run, "agent-c", "password=hunter2", _start.AddMinutes(2), _terms).Run!;
        run = controller.RecordIdea(run, "agent-c", "shard the index", _start.AddMinutes(3), _terms).Run!;
        run = controller.MarkForCard(run, 2, markedByOwner: true).Run!;
        run = controller.MarkForCard(run, 0, markedByOwner: true).Run!;
        return controller.Plan(run);
    }

    [Fact]
    public async Task Applies_in_order_open_then_append_then_wake_then_cards()
    {
        var plan = PlanWithIdeas();
        var sink = new FakeSink();

        await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, CancellationToken.None);

        Assert.Equal(["open", "append", "wake", "card", "card"], sink.Calls);
    }

    [Fact]
    public async Task Only_kept_ideas_are_appended_and_quarantined_ideas_never_reach_the_gc()
    {
        var plan = PlanWithIdeas();
        var sink = new FakeSink();

        await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, CancellationToken.None);

        Assert.Equal([0, 2], sink.Appended.Select(e => e.IdeaIndex));
        Assert.DoesNotContain(sink.Appended, e => e.Text.Contains("hunter2"));
        Assert.All(sink.Appended, e => Assert.Equal("agent", e.SourceTrust));
    }

    [Fact]
    public async Task Wakes_go_only_to_opted_in_agents()
    {
        var plan = PlanWithIdeas();
        var sink = new FakeSink();

        var result = await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, CancellationToken.None);

        Assert.Equal(["agent-b", "agent-c"], sink.Woken.Select(w => w.AgentId));
        Assert.Equal(2, result.WakesInserted);
    }

    [Fact]
    public async Task Cards_point_at_their_idea_message_in_the_brainstorm_gc()
    {
        var plan = PlanWithIdeas();
        var sink = new FakeSink();

        var result = await LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, CancellationToken.None);

        Assert.Equal(2, sink.Cards.Count);
        Assert.All(sink.Cards, c => Assert.Equal(sink.GcId, c.Gc));
        Assert.Equal(["cache per branch", "shard the index"], sink.Cards.Select(c => c.Title));
        Assert.Equal(sink.GcId, result.GcId);
        Assert.Equal(2, result.CardIds.Length);
    }

    [Fact]
    public async Task A_run_with_no_ideas_or_wakes_opens_the_gc_and_stops()
    {
        var controller = new LuckyController(new LuckySettings(), new TextSanitizer());
        var nobody = new[] { new LuckyCandidate("agent-a", OnShift: true, OptedIn: false) };
        var run = controller.Start("run-2", "prompt", true, _start, 0, nobody, () => 1).Run!;
        var sink = new FakeSink();

        var result = await LuckyRunApplier.ApplyAsync(controller.Plan(run), _project, _owner, sink, CancellationToken.None);

        Assert.Equal(["open"], sink.Calls);
        Assert.Equal(0, result.WakesInserted);
        Assert.Empty(result.CardIds);
    }

    [Fact]
    public async Task A_card_for_an_idea_that_was_not_stored_is_refused()
    {
        var plan = PlanWithIdeas();
        var sink = new DroppingSink();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LuckyRunApplier.ApplyAsync(plan, _project, _owner, sink, CancellationToken.None));
    }

    private sealed class DroppingSink : ILuckyRunSink
    {
        public Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, Guid ownerId, CancellationToken ct) =>
            Task.FromResult(new LuckyGcOpened(Guid.NewGuid(), 1));

        public Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LuckyStoredEntry>>([]);

        public Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());
    }
}
