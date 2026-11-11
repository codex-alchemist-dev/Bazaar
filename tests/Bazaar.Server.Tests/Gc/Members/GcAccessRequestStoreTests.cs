using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Members;

namespace Bazaar.Server.Tests.Gc.Members;

/// <summary>Tests for <see cref="GcAccessRequestStore"/> over <c>gc_access_request</c>.</summary>
public sealed class GcAccessRequestStoreTests(GcDb db) : IClassFixture<GcDb>
{
    [Fact]
    public async Task Create_records_a_pending_request_for_the_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var requester = Guid.CreateVersion7();
        var store = new GcAccessRequestStore(db.DataSource);

        var id = await store.CreateAsync(gc, requester, ct);

        var row = Assert.Single(await store.ListPendingAsync(gc, ct));
        Assert.Equal(id, row.Id);
        Assert.Equal(gc, row.GcId);
        Assert.Equal(requester, row.Requester);
        Assert.Equal("pending", row.Status);
        Assert.Equal("pending", await db.ScalarAsync<string>("SELECT status FROM gc_access_request WHERE id = $1", id));
    }

    [Fact]
    public async Task List_returns_only_pending_requests_for_that_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var otherGc = await db.CreateGcAsync();
        var store = new GcAccessRequestStore(db.DataSource);

        var first = await store.CreateAsync(gc, Guid.CreateVersion7(), ct);
        var second = await store.CreateAsync(gc, Guid.CreateVersion7(), ct);
        var third = await store.CreateAsync(gc, Guid.CreateVersion7(), ct);
        await store.CreateAsync(otherGc, Guid.CreateVersion7(), ct);
        Assert.True(await store.ResolveAsync(second, GcAccessDecision.Denied, Guid.CreateVersion7(), DateTimeOffset.UtcNow, ct));

        var pending = await store.ListPendingAsync(gc, ct);

        Assert.Equal([first, third], pending.Select(r => r.Id).ToArray());
        Assert.All(pending, r => Assert.Equal(gc, r.GcId));
    }

    [Fact]
    public async Task Resolve_records_the_decision_once_and_a_second_resolve_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var owner = Guid.CreateVersion7();
        var store = new GcAccessRequestStore(db.DataSource);
        var id = await store.CreateAsync(gc, Guid.CreateVersion7(), ct);
        var decidedAt = DateTimeOffset.UtcNow;

        Assert.True(await store.ResolveAsync(id, GcAccessDecision.Approved, owner, decidedAt, ct));
        Assert.False(await store.ResolveAsync(id, GcAccessDecision.Denied, Guid.CreateVersion7(), decidedAt, ct));

        Assert.Equal("approved", await db.ScalarAsync<string>("SELECT status FROM gc_access_request WHERE id = $1", id));
        Assert.Equal(owner, await db.ScalarAsync<Guid>("SELECT decided_by FROM gc_access_request WHERE id = $1", id));
        Assert.Empty(await store.ListPendingAsync(gc, ct));
    }

    [Fact]
    public async Task Create_for_an_unknown_gc_is_rejected_and_records_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var unknownGc = Guid.CreateVersion7();
        var store = new GcAccessRequestStore(db.DataSource);

        await Assert.ThrowsAsync<GcNotFoundException>(() => store.CreateAsync(unknownGc, Guid.CreateVersion7(), ct));

        Assert.Empty(await store.ListPendingAsync(unknownGc, ct));
    }
}
