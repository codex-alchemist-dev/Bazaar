using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Mirror.Tests;

public class MirrorTests
{
    private readonly InMemoryMirrorStore _store = new();
    private readonly FakeTimeProvider _time = new();

    private MirrorService Make(MirrorOptions? options = null, IMirrorRewriter? rewriter = null) =>
        new(_store, options ?? new MirrorOptions(), _time, rewriter);

    private static MirrorSignal Sig(string topic, MirrorDirection d = MirrorDirection.Avoid, int n = 0, MirrorSignalKind k = MirrorSignalKind.CardRejected) =>
        new(Guid.NewGuid(), k, topic, d, $"card-{topic}-{n}", 1, "reason", DateTimeOffset.UnixEpoch);

    private static async Task Feed(MirrorService s, string topic, MirrorDirection d, int count)
    {
        for (var i = 0; i < count; i++) { await s.RecordAsync(Sig(topic, d, i), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Rule_below_threshold_does_not_apply()
    {
        var s = Make();
        await Feed(s, "tone:chatty", MirrorDirection.Avoid, 4);

        var result = await s.AnnotateAsync([new MirrorItem("a", "hi!", ["tone:chatty"])], TestContext.Current.CancellationToken);

        Assert.Empty(result[0].Annotations);
        Assert.Equal(0, result[0].Score);
    }

    [Fact]
    public async Task Rule_at_threshold_annotates_with_its_count_and_ranks_down()
    {
        var s = Make();
        await Feed(s, "tone:chatty", MirrorDirection.Avoid, 5);
        await Feed(s, "scope:small-prs", MirrorDirection.Prefer, 6);

        var result = await s.AnnotateAsync(
            [new MirrorItem("chatty", "x", ["tone:chatty"]), new MirrorItem("plain", "y", []), new MirrorItem("small", "z", ["scope:small-prs"])],
            TestContext.Current.CancellationToken);

        Assert.Equal(["small", "plain", "chatty"], result.Select(r => r.Original.Id));
        Assert.Equal(5, result[2].Annotations.Single().Count);
    }

    [Fact]
    public async Task Annotation_never_removes_an_item_and_keeps_the_original()
    {
        var s = Make();
        await Feed(s, "bad", MirrorDirection.Avoid, 50);
        var items = Enumerable.Range(0, 20).Select(i => new MirrorItem($"i{i}", $"text {i}", i % 2 == 0 ? ["bad"] : [])).ToList();

        var result = await s.AnnotateAsync(items, TestContext.Current.CancellationToken);

        Assert.Equal(items.Count, result.Count);
        Assert.Equal(items.Select(i => i.Id).Order(), result.Select(r => r.Original.Id).Order());
        Assert.All(result, r => Assert.Equal(items.Single(i => i.Id == r.Original.Id), r.Original));
    }

    [Fact]
    public async Task Ties_keep_input_order()
    {
        var s = Make();
        var result = await s.AnnotateAsync([new MirrorItem("1", "a", []), new MirrorItem("2", "b", []), new MirrorItem("3", "c", [])], TestContext.Current.CancellationToken);
        Assert.Equal(["1", "2", "3"], result.Select(r => r.Original.Id));
    }

    [Fact]
    public async Task Wipe_removes_profile_and_signals()
    {
        var s = Make();
        await Feed(s, "t", MirrorDirection.Avoid, 5);
        Assert.NotNull(await s.GetProfileAsync(TestContext.Current.CancellationToken));

        await s.WipeAsync(TestContext.Current.CancellationToken);

        Assert.Null(await s.GetProfileAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _store.ListSignalsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Paused_learning_and_disabled_kinds_record_nothing()
    {
        var s = Make(new MirrorOptions { SignalKinds = new HashSet<MirrorSignalKind> { MirrorSignalKind.CardRejected } });
        Assert.False(await s.RecordAsync(Sig("t", k: MirrorSignalKind.OwnerEdit), TestContext.Current.CancellationToken));

        await s.SetPausedAsync(true, TestContext.Current.CancellationToken);
        Assert.False(await s.RecordAsync(Sig("t"), TestContext.Current.CancellationToken));
        Assert.Empty(await _store.ListSignalsAsync(TestContext.Current.CancellationToken));

        await s.SetPausedAsync(false, TestContext.Current.CancellationToken);
        Assert.True(await s.RecordAsync(Sig("t"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleted_rule_stays_gone_after_new_signals()
    {
        var s = Make();
        await Feed(s, "t", MirrorDirection.Avoid, 5);
        await s.DeleteRuleAsync("t", MirrorDirection.Avoid, TestContext.Current.CancellationToken);
        await Feed(s, "t", MirrorDirection.Avoid, 3);

        Assert.Empty((await s.GetProfileAsync(TestContext.Current.CancellationToken))!.Rules);
    }

    [Fact]
    public async Task Conflicting_directions_apply_only_the_clear_leader()
    {
        var s = Make();
        await Feed(s, "t", MirrorDirection.Avoid, 5);
        await Feed(s, "t", MirrorDirection.Prefer, 5);
        Assert.Empty((await s.GetProfileAsync(TestContext.Current.CancellationToken))!.Rules);

        await Feed(s, "t", MirrorDirection.Prefer, 2);
        Assert.Equal(MirrorDirection.Prefer, (await s.GetProfileAsync(TestContext.Current.CancellationToken))!.Rules.Single().Direction);
    }

    [Fact]
    public async Task Profile_versions_increase_and_rules_carry_evidence()
    {
        var s = Make();
        await Feed(s, "t", MirrorDirection.Avoid, 5);
        var p = (await s.GetProfileAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(5, p.Version);
        Assert.Equal(5, p.Rules.Single().EvidenceRefs.Count);
    }

    private sealed class Rewriter(bool hosted) : IMirrorRewriter
    {
        public bool IsHosted => hosted;
        public Task<string?> RewriteAsync(MirrorItem item, IReadOnlyList<MirrorRule> rules, CancellationToken ct) => Task.FromResult<string?>("rewritten " + item.Text);
    }

    [Fact]
    public async Task Rewrite_is_a_separate_offer_and_respects_the_setting()
    {
        var items = new[] { new MirrorItem("a", "orig", ["t"]) };

        var on = Make(rewriter: new Rewriter(false));
        await Feed(on, "t", MirrorDirection.Avoid, 5);
        var r = (await on.AnnotateAsync(items, TestContext.Current.CancellationToken))[0];
        Assert.Equal("orig", r.Original.Text);
        Assert.Equal("rewritten orig", r.RewriteOffer!.Text);

        var off = Make(new MirrorOptions { AllowRewriteOffers = false }, new Rewriter(false));
        Assert.Null((await off.AnnotateAsync(items, TestContext.Current.CancellationToken))[0].RewriteOffer);
    }

    [Fact]
    public async Task Hosted_rewriter_needs_operator_approval()
    {
        var items = new[] { new MirrorItem("a", "orig", ["t"]) };
        var local = Make(rewriter: new Rewriter(true));
        await Feed(local, "t", MirrorDirection.Avoid, 5);
        Assert.Null((await local.AnnotateAsync(items, TestContext.Current.CancellationToken))[0].RewriteOffer);

        var approved = Make(new MirrorOptions { ModelSource = MirrorModelSource.OperatorApprovedHosted }, new Rewriter(true));
        Assert.NotNull((await approved.AnnotateAsync(items, TestContext.Current.CancellationToken))[0].RewriteOffer);
    }

    [Fact]
    public void Component_reads_settings_with_safe_fallbacks()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Mirror:SignalKinds"] = "CardAccepted, nonsense",
            ["Bazaar:Mirror:EvidenceThreshold"] = "0",
            ["Bazaar:Mirror:ModelSource"] = "OperatorApprovedHosted",
        }).Build();

        var o = MirrorComponent.ReadOptions(cfg);

        Assert.Equal([MirrorSignalKind.CardAccepted], o.SignalKinds);
        Assert.Equal(5, o.EvidenceThreshold);
        Assert.Equal(MirrorModelSource.OperatorApprovedHosted, o.ModelSource);
        Assert.Equal(MirrorModelSource.Local, MirrorComponent.ReadOptions(new ConfigurationBuilder().Build()).ModelSource);
    }

    [Fact]
    public void Component_registers_the_service()
    {
        var services = new ServiceCollection();
        new MirrorComponent().AddServices(services, new ConfigurationBuilder().Build());
        Assert.NotNull(services.BuildServiceProvider().GetRequiredService<IMirrorService>());
    }
}
