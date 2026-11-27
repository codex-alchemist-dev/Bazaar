using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync;

namespace Bazaar.Server.OfflineSync.Tests;

public class OfflineSyncProcessorTests
{
    private static readonly Guid _device = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Replay_returns_duplicate_and_does_not_call_rule_twice()
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var audit = new CountingAudit();
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule], audit);
        var batch = Make.Batch(_device, [Make.Write(_device, 1), Make.Write(_device, 2)]);

        var first = await processor.ProcessAsync(batch, TestContext.Current.CancellationToken);
        var second = await processor.ProcessAsync(batch, TestContext.Current.CancellationToken);

        Assert.All(first.Outcomes, o => Assert.Equal(OfflineOutcomeKind.Applied, o.Kind));
        Assert.All(second.Outcomes, o => Assert.Equal(OfflineOutcomeKind.Duplicate, o.Kind));
        Assert.Equal(2, rule.Applied.Count);
        Assert.Equal(2, audit.Count);
        Assert.Equal(2, second.AckedSequence);
        Assert.Null(second.GapAtSequence);
    }

    [Fact]
    public async Task Gap_stops_batch_and_reports_expected_sequence()
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule]);
        var batch = Make.Batch(_device, [Make.Write(_device, 1), Make.Write(_device, 3), Make.Write(_device, 4)]);

        var result = await processor.ProcessAsync(batch, TestContext.Current.CancellationToken);

        Assert.Single(result.Outcomes);
        Assert.Equal(1, result.AckedSequence);
        Assert.Equal(2, result.GapAtSequence);
        Assert.Single(rule.Applied);
    }

    [Fact]
    public async Task Unseen_write_on_used_sequence_is_rejected()
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule]);
        await processor.ProcessAsync(Make.Batch(_device, [Make.Write(_device, 1)]), TestContext.Current.CancellationToken);

        var result = await processor.ProcessAsync(Make.Batch(_device, [Make.Write(_device, 1)]), TestContext.Current.CancellationToken);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(OfflineOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("sequence already used", outcome.Reason);
        Assert.Single(rule.Applied);
    }

    [Fact]
    public async Task Clock_skew_is_held_for_review_without_applying()
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var options = new OfflineSyncOptions { MaxClockSkew = TimeSpan.FromMinutes(5) };
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule], options: options);
        var future = Make.Now + (6 * 60_000);

        var result = await processor.ProcessAsync(Make.Batch(_device, [Make.Write(_device, 1, wall: future), Make.Write(_device, 2)]), TestContext.Current.CancellationToken);

        Assert.Equal(OfflineOutcomeKind.HeldForReview, result.Outcomes[0].Kind);
        Assert.Equal(OfflineOutcomeKind.Applied, result.Outcomes[1].Kind);
        Assert.Equal(2, result.AckedSequence);
        Assert.Single(rule.Applied);
    }

    [Fact]
    public async Task Missing_rule_yields_module_unavailable_and_holds_the_sequence()
    {
        var rule = new FakeRule(OfflineDataClass.Preference);
        var store = new InMemoryOfflineSyncStore();
        var processor = Make.Processor(store, [rule]);
        var batch = Make.Batch(_device, [Make.Write(_device, 1), Make.Write(_device, 2, OfflineDataClass.KanbanField), Make.Write(_device, 3)]);

        var result = await processor.ProcessAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal([OfflineOutcomeKind.Applied, OfflineOutcomeKind.ModuleUnavailable], result.Outcomes.Select(o => o.Kind));
        Assert.Equal(1, result.AckedSequence);
        Assert.Null(result.GapAtSequence);
        Assert.Equal(1, await store.GetAckedSequenceAsync(_device, TestContext.Current.CancellationToken));
        Assert.Single(rule.Applied);
    }

    [Fact]
    public async Task A_write_held_for_a_missing_module_applies_once_the_module_is_there()
    {
        var store = new InMemoryOfflineSyncStore();
        var write = Make.Write(_device, 1, OfflineDataClass.KanbanField);
        var ct = TestContext.Current.CancellationToken;

        var held = await Make.Processor(store, []).ProcessAsync(Make.Batch(_device, [write]), ct);
        var later = await Make.Processor(store, [new FakeRule(OfflineDataClass.KanbanField)]).ProcessAsync(Make.Batch(_device, [write]), ct);

        Assert.Equal(OfflineOutcomeKind.ModuleUnavailable, Assert.Single(held.Outcomes).Kind);
        Assert.Equal(OfflineOutcomeKind.Applied, Assert.Single(later.Outcomes).Kind);
        Assert.Equal(1, later.AckedSequence);
    }

    [Fact]
    public async Task Concurrent_batches_for_one_device_do_not_interleave()
    {
        var rule = new FakeRule(OfflineDataClass.Preference, TimeSpan.FromMilliseconds(2));
        var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule]);
        var all = Enumerable.Range(1, 20).Select(i => Make.Write(_device, i)).ToList();
        var ct = TestContext.Current.CancellationToken;

        var a = Task.Run(() => processor.ProcessAsync(Make.Batch(_device, all), ct).AsTask(), ct);
        var b = Task.Run(() => processor.ProcessAsync(Make.Batch(_device, all), ct).AsTask(), ct);
        var results = await Task.WhenAll(a, b);

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), rule.Applied.Select(w => w.DeviceSequence));
        Assert.All(results, r => Assert.Equal(20, r.AckedSequence));
        Assert.Equal(20, results.Sum(r => r.Outcomes.Count(o => o.Kind == OfflineOutcomeKind.Applied)));
        Assert.Equal(20, results.Sum(r => r.Outcomes.Count(o => o.Kind == OfflineOutcomeKind.Duplicate)));
    }

    [Fact]
    public async Task Chaos_drop_after_every_prefix_then_resend_applies_each_write_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var k = 0; k <= 20; k++)
        {
            var rule = new FakeRule(OfflineDataClass.Preference);
            var processor = Make.Processor(new InMemoryOfflineSyncStore(), [rule]);
            var writes = Enumerable.Range(1, 20).Select(i => Make.Write(_device, i)).ToList();

            if (k > 0)
            {
                await processor.ProcessAsync(Make.Batch(_device, writes.Take(k)), ct);
            }

            var result = await processor.ProcessAsync(Make.Batch(_device, writes), ct);

            Assert.Equal(20, result.AckedSequence);
            Assert.Null(result.GapAtSequence);
            Assert.Equal(20, result.Outcomes.Count);
            Assert.Equal(writes.Select(w => w.WriteId), rule.Applied.Select(w => w.WriteId));
        }
    }

    [Fact]
    public void Max_clock_skew_floor_is_enforced()
    {
        var options = new OfflineSyncOptions();
        Assert.Equal(TimeSpan.FromHours(24), options.MaxClockSkew);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxClockSkew = TimeSpan.FromSeconds(59));
    }
}
