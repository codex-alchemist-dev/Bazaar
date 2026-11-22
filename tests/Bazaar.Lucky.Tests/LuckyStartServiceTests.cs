using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Sanitization;

namespace Bazaar.Lucky.Tests;

public class LuckyStartServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid _project = Guid.Parse("00000000-0000-0000-0000-00000000f00d");
    private static readonly LuckyCandidate[] _candidates =
    [
        new("00000000-0000-0000-0000-0000000000a1", OnShift: false, OptedIn: true),
        new("00000000-0000-0000-0000-0000000000a2", OnShift: true, OptedIn: false),
    ];

    private sealed class MemoryStore : ILuckyRunStore
    {
        public List<LuckyStoredRun> Runs { get; } = [];

        public Task<int> CountStartedOnAsync(Guid ownerId, DateOnly day, CancellationToken ct) =>
            Task.FromResult(Runs.Count(r => r.OwnerId == ownerId && r.RunDay == day));

        public Task SaveAsync(LuckyStoredRun stored, CancellationToken ct)
        {
            Runs.RemoveAll(r => r.Run.RunId == stored.Run.RunId);
            Runs.Add(stored);
            return Task.CompletedTask;
        }

        public Task<LuckyStoredRun?> LoadAsync(string runId, CancellationToken ct) =>
            Task.FromResult(Runs.FirstOrDefault(r => r.Run.RunId == runId));
    }

    private sealed class CountingSink : ILuckyRunSink
    {
        public int Opened { get; private set; }

        public Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, Guid ownerId, CancellationToken ct)
        {
            Opened++;
            return Task.FromResult(new LuckyGcOpened(Guid.NewGuid(), 5));
        }

        public Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LuckyStoredEntry>>([]);

        public Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct) =>
            Task.FromResult(wakes.Count);

        public Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());
    }

    private sealed class FixedCandidates(IReadOnlyList<LuckyCandidate> list) : ILuckyCandidateSource
    {
        public Task<IReadOnlyList<LuckyCandidate>> ListAsync(CancellationToken ct) => Task.FromResult(list);
    }

    private static (LuckyStartService Service, MemoryStore Store, CountingSink Sink) Build(LuckySettings? settings = null)
    {
        var store = new MemoryStore();
        var sink = new CountingSink();
        var controller = new LuckyController(settings ?? new LuckySettings(), new TextSanitizer());
        return (new LuckyStartService(controller, store, sink, new FixedCandidates(_candidates)), store, sink);
    }

    [Fact]
    public async Task A_non_owner_cannot_start_and_nothing_is_stored_or_applied()
    {
        var (service, store, sink) = Build();

        var outcome = await service.StartAsync(false, _owner, _project, "stuck on the cache", _now, CancellationToken.None);

        Assert.Equal(LuckyStartStatus.NotOwner, outcome.Status);
        Assert.Empty(store.Runs);
        Assert.Equal(0, sink.Opened);
    }

    [Fact]
    public async Task The_owner_starts_a_run_that_is_stored_and_applied()
    {
        var (service, store, sink) = Build();

        var outcome = await service.StartAsync(true, _owner, _project, "stuck on the cache", _now, CancellationToken.None);

        Assert.Equal(LuckyStartStatus.Started, outcome.Status);
        Assert.NotNull(outcome.RunId);
        Assert.Equal(2, outcome.Participants);
        Assert.Equal(1, outcome.WakesInserted);
        var stored = Assert.Single(store.Runs);
        Assert.Equal(_project, stored.ProjectId);
        Assert.Equal(_owner, stored.OwnerId);
        Assert.Equal(new DateOnly(2026, 11, 22), stored.RunDay);
        Assert.Equal(1, sink.Opened);
    }

    [Fact]
    public async Task The_daily_cap_refuses_a_second_start_on_the_same_day()
    {
        var (service, store, sink) = Build();
        await service.StartAsync(true, _owner, _project, "first", _now, CancellationToken.None);

        var second = await service.StartAsync(true, _owner, _project, "second", _now.AddHours(2), CancellationToken.None);

        Assert.Equal(LuckyStartStatus.Refused, second.Status);
        Assert.Single(store.Runs);
        Assert.Equal(1, sink.Opened);
    }

    [Fact]
    public async Task The_cap_resets_on_the_next_utc_day()
    {
        var (service, store, _) = Build();
        await service.StartAsync(true, _owner, _project, "first", _now, CancellationToken.None);

        var nextDay = await service.StartAsync(true, _owner, _project, "tomorrow", _now.AddDays(1), CancellationToken.None);

        Assert.Equal(LuckyStartStatus.Started, nextDay.Status);
        Assert.Equal(2, store.Runs.Count);
    }

    [Fact]
    public async Task An_empty_prompt_is_refused_before_anything_is_stored()
    {
        var (service, store, sink) = Build();

        var outcome = await service.StartAsync(true, _owner, _project, "  ", _now, CancellationToken.None);

        Assert.Equal(LuckyStartStatus.Refused, outcome.Status);
        Assert.Empty(store.Runs);
        Assert.Equal(0, sink.Opened);
    }
}
