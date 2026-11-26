using System.Collections.Immutable;
using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Sanitization;

namespace Bazaar.Lucky.Tests;

public class LuckyIdeaServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new([], [], [], [], []);
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid _project = Guid.Parse("00000000-0000-0000-0000-00000000f00d");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid _agentB = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid _agentC = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private sealed class MemoryStore : ILuckyRunStore
    {
        public Dictionary<string, LuckyStoredRun> Runs { get; } = [];

        public Task<int> CountStartedOnAsync(Guid ownerId, DateOnly day, CancellationToken ct) => Task.FromResult(0);

        public Task SaveAsync(LuckyStoredRun stored, CancellationToken ct)
        {
            Runs[stored.Run.RunId] = stored;
            return Task.CompletedTask;
        }

        public Task<LuckyStoredRun?> LoadAsync(string runId, CancellationToken ct) =>
            Task.FromResult(Runs.TryGetValue(runId, out var r) ? r : null);
    }

    private sealed class RecordingSink : ILuckyRunSink
    {
        public List<LuckyGcEntry> Appended { get; } = [];
        public List<(Guid Message, string Title)> Cards { get; } = [];

        public Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, Guid ownerId, CancellationToken ct) =>
            Task.FromResult(new LuckyGcOpened(_gc, 1));

        public Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct)
        {
            Appended.AddRange(entries);
            IReadOnlyList<LuckyStoredEntry> stored = entries
                .Select(e => new LuckyStoredEntry(e.IdeaIndex, Guid.Parse($"00000000-0000-0000-0000-{(e.IdeaIndex + 500):000000000000}")))
                .ToList();
            return Task.FromResult(stored);
        }

        public Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct) =>
            Task.FromResult(wakes.Count);

        public Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct)
        {
            Cards.Add((messageId, title));
            return Task.FromResult(Guid.Parse("00000000-0000-0000-0000-0000000000dd"));
        }
    }

    /// <summary>A run already applied: GC open, two participants, no ideas yet.</summary>
    private static (LuckyIdeaService Service, MemoryStore Store, RecordingSink Sink, string RunId) Seeded(LuckySettings? settings = null)
    {
        var controller = new LuckyController(settings ?? new LuckySettings(), new TextSanitizer());
        var candidates = new[]
        {
            new LuckyCandidate(_agentB.ToString(), OnShift: true, OptedIn: true),
            new LuckyCandidate(_agentC.ToString(), OnShift: true, OptedIn: false),
        };
        var run = controller.Start("run-idea", "stuck on the cache", true, _now, 0, candidates, () => 7).Run!;
        var store = new MemoryStore();
        store.Runs[run.RunId] = new LuckyStoredRun(run, _project, _owner, new DateOnly(2026, 11, 22), _gc, ImmutableDictionary<int, Guid>.Empty);
        var sink = new RecordingSink();
        return (new LuckyIdeaService(controller, store, sink), store, sink, run.RunId);
    }

    [Fact]
    public async Task A_kept_idea_is_appended_to_the_brainstorm_gc_at_once()
    {
        var (service, store, sink, runId) = Seeded();

        var outcome = await service.RecordIdeaAsync(_agentB, runId, "cache per branch", _now.AddMinutes(1), _terms, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Done, outcome.Status);
        Assert.False(outcome.Quarantined);
        Assert.Equal(0, outcome.IdeaIndex);
        var entry = Assert.Single(sink.Appended);
        Assert.Equal("cache per branch", entry.Text);
        Assert.Equal(new Guid("00000000-0000-0000-0000-000000000500"), store.Runs[runId].Messages[0]);
    }

    [Fact]
    public async Task A_quarantined_idea_is_kept_for_the_limit_but_never_reaches_the_gc()
    {
        var (service, store, sink, runId) = Seeded();

        var outcome = await service.RecordIdeaAsync(_agentB, runId, "password=hunter2", _now.AddMinutes(1), _terms, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Done, outcome.Status);
        Assert.True(outcome.Quarantined);
        Assert.Empty(sink.Appended);
        Assert.Single(store.Runs[runId].Run.Ideas);
    }

    [Fact]
    public async Task A_non_participant_cannot_record_an_idea()
    {
        var (service, _, sink, runId) = Seeded(new LuckySettings { Participation = LuckyParticipation.OptedInOnly });

        var outcome = await service.RecordIdeaAsync(_agentC, runId, "an idea", _now.AddMinutes(1), _terms, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Refused, outcome.Status);
        Assert.Empty(sink.Appended);
    }

    [Fact]
    public async Task An_unknown_run_is_not_found()
    {
        var (service, _, _, _) = Seeded();

        var outcome = await service.RecordIdeaAsync(_agentB, "no-such-run", "an idea", _now.AddMinutes(1), _terms, CancellationToken.None);

        Assert.Equal(LuckyActStatus.NotFound, outcome.Status);
    }

    [Fact]
    public async Task The_owner_marks_a_kept_idea_and_it_becomes_a_backlog_card_from_its_message()
    {
        var (service, _, sink, runId) = Seeded();
        await service.RecordIdeaAsync(_agentB, runId, "cache per branch", _now.AddMinutes(1), _terms, CancellationToken.None);

        var outcome = await service.MarkIdeaAsync(true, runId, 0, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Done, outcome.Status);
        Assert.NotNull(outcome.CardId);
        var card = Assert.Single(sink.Cards);
        Assert.Equal("cache per branch", card.Title);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000500"), card.Message);
    }

    [Fact]
    public async Task Only_the_owner_can_mark()
    {
        var (service, _, sink, runId) = Seeded();
        await service.RecordIdeaAsync(_agentB, runId, "cache per branch", _now.AddMinutes(1), _terms, CancellationToken.None);

        var outcome = await service.MarkIdeaAsync(false, runId, 0, CancellationToken.None);

        Assert.Equal(LuckyActStatus.NotAllowed, outcome.Status);
        Assert.Empty(sink.Cards);
    }

    [Fact]
    public async Task A_quarantined_idea_cannot_be_marked()
    {
        var (service, _, sink, runId) = Seeded();
        await service.RecordIdeaAsync(_agentB, runId, "password=hunter2", _now.AddMinutes(1), _terms, CancellationToken.None);

        var outcome = await service.MarkIdeaAsync(true, runId, 0, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Refused, outcome.Status);
        Assert.Empty(sink.Cards);
    }

    [Fact]
    public async Task Marking_twice_does_not_stop_the_card_from_being_keyed_to_the_same_message()
    {
        var (service, _, sink, runId) = Seeded();
        await service.RecordIdeaAsync(_agentB, runId, "cache per branch", _now.AddMinutes(1), _terms, CancellationToken.None);

        await service.MarkIdeaAsync(true, runId, 0, CancellationToken.None);
        var second = await service.MarkIdeaAsync(true, runId, 0, CancellationToken.None);

        Assert.Equal(LuckyActStatus.Done, second.Status);
        Assert.Equal(2, sink.Cards.Count);
        Assert.Equal(sink.Cards[0].Message, sink.Cards[1].Message);
    }
}
