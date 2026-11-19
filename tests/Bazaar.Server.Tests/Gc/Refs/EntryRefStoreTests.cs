using System.Text.Json;
using Bazaar.Server.Gc.Refs;

namespace Bazaar.Server.Tests.Gc.Refs;

/// <summary>Tests for <see cref="EntryRefStore"/> over <c>entry_ref</c>.</summary>
public sealed class EntryRefStoreTests(GcDb db) : IClassFixture<GcDb>
{
    private const string _prSnapshot = """{"state": "open", "title": "Add refs"}""";

    [Fact]
    public async Task Insert_then_read_returns_the_reference_with_its_snapshot()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new EntryRefStore(db.DataSource);
        var entry = new EntryRef(gc, 7, EntryRefKinds.Pr, "codex-alchemist-dev/Bazaar#42", _prSnapshot);

        Assert.True(await store.InsertAsync(entry, ct));

        var refs = await store.GetForMessageAsync(gc, 7, ct);
        var read = Assert.Single(refs);
        Assert.Equal(gc, read.GcId);
        Assert.Equal(7, read.Seq);
        Assert.Equal(EntryRefKinds.Pr, read.RefKind);
        Assert.Equal("codex-alchemist-dev/Bazaar#42", read.RefId);
        using var snapshot = JsonDocument.Parse(read.SnapshotJson);
        Assert.Equal("open", snapshot.RootElement.GetProperty("state").GetString());
        Assert.Equal("Add refs", snapshot.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Duplicate_insert_is_a_no_op_that_keeps_the_first_snapshot()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new EntryRefStore(db.DataSource);
        var first = new EntryRef(gc, 3, EntryRefKinds.Task, "T-1", """{"column": "Ready"}""");
        var repeat = new EntryRef(gc, 3, EntryRefKinds.Task, "T-1", """{"column": "Done"}""");

        Assert.True(await store.InsertAsync(first, ct));
        Assert.False(await store.InsertAsync(repeat, ct));

        var read = Assert.Single(await store.GetForMessageAsync(gc, 3, ct));
        using var snapshot = JsonDocument.Parse(read.SnapshotJson);
        Assert.Equal("Ready", snapshot.RootElement.GetProperty("column").GetString());
    }

    [Fact]
    public async Task Reads_are_ordered_by_kind_then_id_whatever_the_insert_order()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new EntryRefStore(db.DataSource);
        await store.InsertAsync(new EntryRef(gc, 9, EntryRefKinds.VaultDoc, "docs/b.md", "{}"), ct);
        await store.InsertAsync(new EntryRef(gc, 9, EntryRefKinds.Pr, "PR-20", "{}"), ct);
        await store.InsertAsync(new EntryRef(gc, 9, EntryRefKinds.VaultDoc, "docs/a.md", "{}"), ct);
        await store.InsertAsync(new EntryRef(gc, 9, EntryRefKinds.Pr, "PR-3", "{}"), ct);

        var order = (await store.GetForMessageAsync(gc, 9, ct))
            .Select(r => $"{r.RefKind}:{r.RefId}")
            .ToList();

        // Byte-wise order: "PR-20" sorts before "PR-3", and "pr" before "vault_doc".
        Assert.Equal(["pr:PR-20", "pr:PR-3", "vault_doc:docs/a.md", "vault_doc:docs/b.md"], order);
    }

    [Fact]
    public async Task Reads_only_return_references_for_the_requested_gc_and_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var other = await db.CreateGcAsync();
        var store = new EntryRefStore(db.DataSource);
        await store.InsertAsync(new EntryRef(gc, 5, EntryRefKinds.Decision, "decisions/0001-x.md", "{}"), ct);
        await store.InsertAsync(new EntryRef(gc, 6, EntryRefKinds.Decision, "decisions/0002-y.md", "{}"), ct);
        await store.InsertAsync(new EntryRef(other, 5, EntryRefKinds.Decision, "decisions/0003-z.md", "{}"), ct);

        var read = Assert.Single(await store.GetForMessageAsync(gc, 5, ct));
        Assert.Equal("decisions/0001-x.md", read.RefId);
    }

    [Fact]
    public async Task Unknown_message_returns_empty()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new EntryRefStore(db.DataSource);

        Assert.Empty(await store.GetForMessageAsync(gc, 1, ct));
        Assert.Empty(await store.GetForMessageAsync(Guid.CreateVersion7(), 1, ct));
    }

    [Fact]
    public async Task Unknown_reference_kind_is_rejected_before_it_reaches_the_database()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var store = new EntryRefStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.InsertAsync(new EntryRef(Guid.CreateVersion7(), 1, "email", "x", "{}"), ct));
    }
}
