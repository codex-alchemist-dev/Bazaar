using Bazaar.Server.Embeddings.Indexing;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Embeddings.Tests;

internal sealed class FakeThresholdSource(decimal value) : IEmbeddingSwitchSettingSource
{
    public decimal Value { get; set; } = value;

    public Task<decimal> GetSwitchThresholdAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>Fails with a retry-later error once <paramref name="succeedFor"/> calls have succeeded.</summary>
internal sealed class FlakyProvider(IEmbeddingProvider inner, int succeedFor) : IEmbeddingProvider
{
    private int _calls;

    public List<string> Seen { get; } = [];

    public int SucceedFor { get; set; } = succeedFor;

    public EmbeddingProviderDescriptor Descriptor => inner.Descriptor;

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (_calls++ >= SucceedFor)
        {
            throw new EmbeddingRetryLaterException("provider busy");
        }

        Seen.AddRange(texts);
        return inner.EmbedAsync(texts, cancellationToken);
    }
}

public class ProfileSwitchTests
{
    private static readonly Guid _doc = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static byte[] Hash(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private sealed class Rig
    {
        public Rig(IEmbeddingProvider? newProvider = null, string setting = "new", decimal threshold = 1m, int batch = 2)
        {
            Old = new EmbeddingProfile(Guid.NewGuid(), "old", FakeEmbeddingProvider.DefaultModelId, 8, DateTimeOffset.UnixEpoch, true);
            Profiles = new FakeProfileStore(Old);
            Setting = new FakeSettingSource(setting);
            Threshold = new FakeThresholdSource(threshold);
            NewProvider = newProvider ?? new FakeEmbeddingProvider(16);
            Registry = new FakeRegistry(("old", new FakeEmbeddingProvider()), ("new", NewProvider));
            Batch = batch;
        }

        public EmbeddingProfile Old { get; }

        public FakeProfileStore Profiles { get; }

        public FakeSettingSource Setting { get; }

        public FakeThresholdSource Threshold { get; }

        public IEmbeddingProvider NewProvider { get; set; }

        public FakeRegistry Registry { get; private set; }

        public int Batch { get; }

        public InMemoryIndexStore Store { get; } = new();

        public EmbeddingStatusTracker Status { get; } = new();

        public ListLogger<EmbeddingIndexer> Log { get; } = new();

        public EmbeddingProfile Target => Profiles.Rows.Single(r => r.ProviderId == "new");

        /// <summary>A fresh indexer over the same store, as after a process restart.</summary>
        public EmbeddingIndexer NewIndexer()
        {
            Registry = new FakeRegistry(("old", new FakeEmbeddingProvider()), ("new", NewProvider));
            var detector = new FencedPersonalDataBlockDetector();
            return new EmbeddingIndexer(
                new ActiveEmbeddingSelector(Setting, Profiles, Registry),
                Store,
                detector,
                Batch,
                new ProfileSwitchPlanner(Setting, Profiles, Registry),
                Profiles,
                Threshold,
                Status,
                Log);
        }

        public void AddBlocks(params string[] bodies)
        {
            for (var i = 0; i < bodies.Length; i++)
            {
                var block = new PendingBlock(_doc, Store.Blocks.Count, bodies[i], Hash((byte)(Store.Blocks.Count + 1)));
                Store.Blocks.Add(block);
            }
        }

        /// <summary>Marks every non-personal block as already embedded under the old profile.</summary>
        public void CoverOld()
        {
            foreach (var b in Store.Blocks.Where(b => !new FencedPersonalDataBlockDetector().IsPersonalData(b.Body)))
            {
                Store.ByProfile[(Old.ProfileId, b.DocId, b.Ordinal)] = b.TextHash;
            }
        }
    }

    [Fact]
    public async Task Backfill_runs_in_keyset_order_and_resumes_after_a_restart_without_re_embedding()
    {
        var provider = new FlakyProvider(new FakeEmbeddingProvider(16), succeedFor: 1);
        var rig = new Rig(provider);
        rig.AddBlocks("b0", "b1", "b2", "b3", "b4");
        rig.CoverOld();

        var first = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.Equal("provider busy", first.Error);
        Assert.Equal(2, first.Embedded);
        Assert.Equal(["b0", "b1"], provider.Seen);
        Assert.False(first.Activated);

        provider.SucceedFor = int.MaxValue;
        provider.Seen.Clear();
        var second = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.Null(second.Error);
        Assert.Equal(["b2", "b3", "b4"], provider.Seen);
        Assert.True(second.Activated);
    }

    [Fact]
    public async Task Search_profile_stays_active_and_nothing_activates_before_coverage_is_reached()
    {
        var provider = new FlakyProvider(new FakeEmbeddingProvider(16), succeedFor: 1);
        var rig = new Rig(provider);
        rig.AddBlocks("b0", "b1", "b2", "b3");
        rig.CoverOld();

        await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, rig.Profiles.Activations);
        Assert.Equal(rig.Old.ProfileId, rig.Profiles.Active!.ProfileId);
        var snapshot = await rig.Status.GetAsync(CancellationToken.None);
        Assert.Equal(2, snapshot.Target!.Embedded);
        Assert.Equal(4, snapshot.Target.Embeddable);
    }

    [Fact]
    public async Task A_sweep_that_ended_in_an_error_never_activates_even_when_the_threshold_is_met()
    {
        var provider = new FlakyProvider(new FakeEmbeddingProvider(16), succeedFor: 1);
        var rig = new Rig(provider, threshold: 0.5m);
        rig.AddBlocks("b0", "b1", "b2", "b3");
        rig.CoverOld();

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(0, rig.Profiles.Activations);

        provider.SucceedFor = int.MaxValue;
        Assert.True((await rig.NewIndexer().RunOnceAsync(CancellationToken.None)).Activated);
    }

    [Fact]
    public async Task Activation_is_one_swap_that_keeps_the_old_profile_and_its_rows()
    {
        var rig = new Rig();
        rig.AddBlocks("b0", "b1", "b2");
        rig.CoverOld();

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.True(result.Activated);
        Assert.Equal(1, rig.Profiles.Activations);
        Assert.Equal(rig.Target.ProfileId, rig.Profiles.Active!.ProfileId);
        Assert.Single(rig.Profiles.Rows, r => r.Active);
        Assert.Contains(rig.Profiles.Rows, r => r.ProfileId == rig.Old.ProfileId && !r.Active);
        Assert.Equal(3, rig.Store.StoredFor(rig.Old.ProfileId));
        Assert.Equal(3, rig.Store.StoredFor(rig.Target.ProfileId));
        Assert.All(rig.Store.UpsertedProfiles, id => Assert.Equal(rig.Target.ProfileId, id));

        var again = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.False(again.Activated);
        Assert.Equal(1, rig.Profiles.Activations);
        Assert.Equal(1, rig.Profiles.Creates);
    }

    [Fact]
    public async Task Personal_data_blocks_are_not_in_the_denominator_and_never_reach_the_provider()
    {
        var provider = new CountingProvider(new FakeEmbeddingProvider(16));
        var rig = new Rig(provider);
        rig.AddBlocks("plain 0", "```bazaar-encrypted\nsecret\n```", "plain 1", "~~~bazaar-encrypted\nsecret\n~~~");
        rig.CoverOld();

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.True(result.Activated);
        Assert.Equal(["plain 0", "plain 1"], provider.Seen);
        var snapshot = await rig.Status.GetAsync(CancellationToken.None);
        Assert.Equal(2, snapshot.Active!.Embeddable);
        Assert.Equal(2, snapshot.Active.Embedded);
        Assert.Equal(2, snapshot.TargetSweep!.SkippedPersonal);
    }

    [Fact]
    public async Task A_too_long_block_is_logged_once_per_block_and_profile_and_not_per_sweep()
    {
        var rig = new Rig(new TooLongProvider(new FakeEmbeddingProvider(16), "huge-text-body"), threshold: 1m);
        rig.AddBlocks("fine", "huge-text-body");
        rig.CoverOld();
        var indexer = rig.NewIndexer();

        for (var i = 0; i < 3; i++)
        {
            var result = await indexer.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, result.TooLong);
        }

        var tooLongLogs = rig.Log.Entries.Where(e => e.Message.Contains("too long", StringComparison.Ordinal)).ToList();
        Assert.Single(tooLongLogs);
        Assert.Contains(rig.Target.ProfileId.ToString(), tooLongLogs[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Log.Entries, e => e.Message.Contains("huge-text-body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Too_long_blocks_are_left_out_of_the_denominator_so_a_full_threshold_can_still_activate()
    {
        var rig = new Rig(new TooLongProvider(new FakeEmbeddingProvider(16), "huge"));
        rig.AddBlocks("fine", "huge");
        rig.CoverOld();

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.True(result.Activated);
        Assert.Equal(1, result.TooLong);
    }

    [Fact]
    public async Task Status_reports_counts_coverage_and_backlog_per_profile()
    {
        var provider = new FlakyProvider(new FakeEmbeddingProvider(16), succeedFor: 1);
        var rig = new Rig(provider);
        rig.AddBlocks("b0", "b1", "b2", "```bazaar-encrypted\nx\n```");
        rig.CoverOld();
        Assert.True((await rig.Status.GetAsync(CancellationToken.None)).Idle);

        await rig.NewIndexer().RunOnceAsync(CancellationToken.None);
        var snapshot = await rig.Status.GetAsync(CancellationToken.None);

        Assert.False(snapshot.Idle);
        Assert.Equal(new EmbeddingCoverage(rig.Old.ProfileId, 3, 3), snapshot.Active);
        Assert.Equal(new EmbeddingCoverage(rig.Target.ProfileId, 2, 3), snapshot.Target);
        Assert.Equal(1, snapshot.Backlog);
        Assert.Equal(1m, snapshot.SwitchThreshold);
        Assert.Equal(0, snapshot.ActiveSweep!.Embedded);
        Assert.Equal(2, snapshot.TargetSweep!.Embedded);
        Assert.Equal(1, snapshot.TargetSweep.SkippedPersonal);
        Assert.Equal(1, snapshot.TargetSweep.RetryLater);
        Assert.Equal("provider busy", snapshot.TargetSweep.Error);
        Assert.Null(snapshot.LastActivatedAt);

        provider.SucceedFor = int.MaxValue;
        var done = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);
        snapshot = await rig.Status.GetAsync(CancellationToken.None);

        Assert.True(done.Activated);
        Assert.Null(snapshot.Target);
        Assert.Equal(rig.Target.ProfileId, snapshot.Active!.ProfileId);
        Assert.Equal(0, snapshot.Backlog);
        Assert.NotNull(snapshot.LastActivatedAt);
    }

    [Fact]
    public async Task Without_a_pending_switch_the_status_shows_the_active_profile_only()
    {
        var rig = new Rig(setting: "old");
        rig.AddBlocks("b0", "b1");

        await rig.NewIndexer().RunOnceAsync(CancellationToken.None);
        var snapshot = await rig.Status.GetAsync(CancellationToken.None);

        Assert.Null(snapshot.Target);
        Assert.Null(snapshot.TargetSweep);
        Assert.Equal(2, snapshot.ActiveSweep!.Embedded);
        Assert.Equal(new EmbeddingCoverage(rig.Old.ProfileId, 2, 2), snapshot.Active);
        Assert.Equal(0, rig.Profiles.Creates);
    }

    [Fact]
    public async Task Setting_none_is_idle_even_with_a_status_tracker()
    {
        var rig = new Rig(setting: "none");
        rig.AddBlocks("b0");

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.True(result.Idle);
        Assert.True((await rig.Status.GetAsync(CancellationToken.None)).Idle);
        Assert.Equal(0, rig.Store.ListCalls);
    }

    [Fact]
    public async Task An_unconfigured_target_provider_is_reported_and_the_active_profile_is_still_kept_current()
    {
        var rig = new Rig(setting: "ghost");
        rig.AddBlocks("b0");

        var result = await rig.NewIndexer().RunOnceAsync(CancellationToken.None);

        Assert.Contains("ghost", result.Error);
        Assert.Equal(0, rig.Profiles.Creates);
    }

    [Fact]
    public async Task Planner_finds_an_existing_target_row_instead_of_creating_another()
    {
        var rig = new Rig();
        var existing = await rig.Profiles.CreateAsync("new", FakeEmbeddingProvider.DefaultModelId, 16, CancellationToken.None);
        var planner = new ProfileSwitchPlanner(rig.Setting, rig.Profiles, new FakeRegistry(("old", new FakeEmbeddingProvider()), ("new", rig.NewProvider)));

        var target = await planner.ResolveTargetAsync(CancellationToken.None);

        Assert.Equal(existing.ProfileId, target!.Profile.ProfileId);
        Assert.Equal(1, rig.Profiles.Creates);
    }

    [Fact]
    public async Task A_same_provider_with_a_new_model_is_a_switch_to_a_new_profile()
    {
        var rig = new Rig(setting: "old");
        var registry = new FakeRegistry(("old", new FakeEmbeddingProvider(8, "bigger-model")));
        var planner = new ProfileSwitchPlanner(rig.Setting, rig.Profiles, registry);

        var target = await planner.ResolveTargetAsync(CancellationToken.None);

        Assert.NotEqual(rig.Old.ProfileId, target!.Profile.ProfileId);
        Assert.Equal("bigger-model", target.Profile.ModelId);
    }

    [Fact]
    public void Switch_threshold_defaults_to_all_blocks_and_must_be_above_zero_and_at_most_one()
    {
        var key = EmbeddingSettings.SwitchThresholdKey;

        Assert.Equal("memory.embedding_switch_threshold", key.Name);
        Assert.Equal(1.0m, key.Default);
        Assert.Contains(key, EmbeddingSettings.All);
        foreach (var (json, ok) in new[] { ("0.9", true), ("1", true), ("0", false), ("1.5", false), ("-0.1", false) })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(ok, key.CheckValue(doc.RootElement) is null);
        }
    }

    [Fact]
    public void Coverage_reaches_a_threshold_exactly_and_an_empty_vault_is_full()
    {
        Assert.True(new EmbeddingCoverage(Guid.Empty, 3, 3).Reaches(1m));
        Assert.False(new EmbeddingCoverage(Guid.Empty, 2, 3).Reaches(1m));
        Assert.True(new EmbeddingCoverage(Guid.Empty, 2, 3).Reaches(0.6m));
        Assert.True(new EmbeddingCoverage(Guid.Empty, 0, 0).Reaches(1m));
        Assert.Equal(1m, new EmbeddingCoverage(Guid.Empty, 0, 0).Ratio);
    }
}
