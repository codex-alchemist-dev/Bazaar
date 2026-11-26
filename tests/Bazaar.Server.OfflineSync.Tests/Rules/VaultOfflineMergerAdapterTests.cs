using Bazaar.Server.OfflineSync.Rules;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync.Tests.Rules;

public sealed class VaultOfflineMergerAdapterTests
{
    private const string _head = "1111111111111111111111111111111111111111";
    private const string _base = "2222222222222222222222222222222222222222";
    private static readonly VaultPushIntent _intent = new(Guid.NewGuid(), Guid.NewGuid(), "vault-main", "agent/x", _head, _base);

    [Fact]
    public async Task Merged_passes_the_new_head_and_opens_no_review_item()
    {
        var review = new RecordingReviewSink();
        var adapter = Adapter(new VaultOfflineMergeResult.Merged("abc"), review, out _);

        var result = await adapter.MergeAsync(_intent, CancellationToken.None);

        Assert.Equal(new VaultMergeResult.Merged("abc"), result);
        Assert.Empty(review.Items);
    }

    [Fact]
    public async Task Conflicted_opens_a_review_item_naming_both_branch_and_paths()
    {
        var review = new RecordingReviewSink();
        var adapter = Adapter(new VaultOfflineMergeResult.Conflicted("conflict/d/1", ["a.md", "b.md"]), review, out _);

        var result = await adapter.MergeAsync(_intent, CancellationToken.None);

        var conflict = Assert.IsType<VaultMergeResult.ConflictBranch>(result);
        Assert.Equal("conflict/d/1", conflict.BranchName);
        var item = Assert.Single(review.Items);
        Assert.Equal(VaultOfflineMergerAdapter.ReviewKind, item.Kind);
        Assert.Equal(_intent.WriteId, item.WriteId);
        Assert.Equal(_base, item.BaseVersion);
        Assert.Equal("conflict/d/1", item.ServerVersion);
        Assert.Contains("a.md, b.md", item.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_passes_the_reason_through()
    {
        var review = new RecordingReviewSink();
        var adapter = Adapter(new VaultOfflineMergeResult.Rejected("no"), review, out _);

        Assert.Equal(new VaultMergeResult.Rejected("no"), await adapter.MergeAsync(_intent, CancellationToken.None));
        Assert.Empty(review.Items);
    }

    [Fact]
    public async Task The_request_carries_the_intent_and_the_clock_time()
    {
        var adapter = Adapter(new VaultOfflineMergeResult.Merged("abc"), new RecordingReviewSink(), out var engine);

        await adapter.MergeAsync(_intent, CancellationToken.None);

        var request = Assert.Single(engine.Requests);
        Assert.Equal((_intent.WriteId, _intent.DeviceId, "agent/x", _head, _base), (request.WriteId, request.DeviceId, request.Branch, request.HeadCommit, request.BaseCommit));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero), request.At);
    }

    [Fact]
    public void Registration_needs_an_engine_and_never_replaces_a_merger()
    {
        Assert.DoesNotContain(new ServiceCollection().AddBazaarVaultOfflineMerger(), d => d.ServiceType == typeof(IVaultOfflineMerger));

        var withEngine = new ServiceCollection().AddSingleton<IVaultOfflineMergeEngine>(new FakeEngine(new VaultOfflineMergeResult.Merged("a")));
        Assert.Single(withEngine.AddBazaarVaultOfflineMerger().AddBazaarVaultOfflineMerger(), d => d.ServiceType == typeof(IVaultOfflineMerger));
    }

    private static VaultOfflineMergerAdapter Adapter(VaultOfflineMergeResult result, RecordingReviewSink review, out FakeEngine engine)
    {
        engine = new FakeEngine(result);
        return new VaultOfflineMergerAdapter(engine, review, new FixedTime(new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero)));
    }

    private sealed class FakeEngine(VaultOfflineMergeResult result) : IVaultOfflineMergeEngine
    {
        public List<VaultOfflineMergeRequest> Requests { get; } = [];

        public ValueTask<VaultOfflineMergeResult> MergeAsync(VaultOfflineMergeRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingReviewSink : IReviewSink
    {
        public List<ReviewItem> Items { get; } = [];

        public ValueTask OpenAsync(ReviewItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
