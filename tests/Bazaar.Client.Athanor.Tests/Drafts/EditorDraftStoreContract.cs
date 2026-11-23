namespace Bazaar.Client.Athanor.Tests.Drafts;

/// <summary>The behaviour both draft stores must share. Each concrete test class supplies its store.</summary>
public abstract class EditorDraftStoreContract
{
    protected abstract IEditorDraftStore Store { get; }

    protected abstract ManualClock Clock { get; }

    protected static EditorDocumentId Doc(string path = "src/a.cs") => new("ws-1", path);

    protected static EditorDraft Draft(EditorDocumentId id, string content, string revision = "r1") =>
        new(id, content, revision, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Write_then_read_returns_the_draft_with_clock_time()
    {
        Clock.Now = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
        await Store.WriteAsync(Draft(Doc(), "hello", "r7"), CancellationToken.None);

        var read = await Store.ReadAsync(Doc(), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(Doc(), read.Id);
        Assert.Equal("hello", read.Content);
        Assert.Equal("r7", read.BaseRevision);
        Assert.Equal(Clock.Now, read.SavedAt);
    }

    [Fact]
    public async Task Read_of_a_missing_draft_returns_null()
    {
        Assert.Null(await Store.ReadAsync(Doc("nope.cs"), CancellationToken.None));
    }

    [Fact]
    public async Task Overwrite_replaces_content_and_keeps_one_draft()
    {
        await Store.WriteAsync(Draft(Doc(), "first"), CancellationToken.None);
        await Store.WriteAsync(Draft(Doc(), "second", "r2"), CancellationToken.None);

        var read = await Store.ReadAsync(Doc(), CancellationToken.None);
        var all = await Store.ListAsync(CancellationToken.None);

        Assert.Equal("second", read!.Content);
        Assert.Equal("r2", read.BaseRevision);
        Assert.Single(all);
    }

    [Fact]
    public async Task Revision_is_not_part_of_the_key()
    {
        await Store.WriteAsync(Draft(Doc(), "against r1", "r1"), CancellationToken.None);
        await Store.WriteAsync(Draft(Doc(), "against r9", "r9"), CancellationToken.None);

        Assert.Single(await Store.ListAsync(CancellationToken.None));
        Assert.Equal("r9", (await Store.ReadAsync(Doc(), CancellationToken.None))!.BaseRevision);
    }

    [Fact]
    public async Task Same_path_in_two_workspaces_is_two_drafts()
    {
        await Store.WriteAsync(Draft(new("ws-a", "x.cs"), "a"), CancellationToken.None);
        await Store.WriteAsync(Draft(new("ws-b", "x.cs"), "b"), CancellationToken.None);

        Assert.Equal("a", (await Store.ReadAsync(new("ws-a", "x.cs"), CancellationToken.None))!.Content);
        Assert.Equal("b", (await Store.ReadAsync(new("ws-b", "x.cs"), CancellationToken.None))!.Content);
        Assert.Equal(2, (await Store.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Delete_removes_the_draft()
    {
        await Store.WriteAsync(Draft(Doc(), "gone"), CancellationToken.None);

        await Store.DeleteAsync(Doc(), CancellationToken.None);

        Assert.Null(await Store.ReadAsync(Doc(), CancellationToken.None));
        Assert.Empty(await Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Delete_of_a_missing_draft_is_a_no_op()
    {
        await Store.DeleteAsync(Doc("never-written.cs"), CancellationToken.None);
        await Store.DeleteAsync(Doc("never-written.cs"), CancellationToken.None);

        Assert.Empty(await Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_is_sorted_by_saved_at_ascending()
    {
        Clock.Now = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        await Store.WriteAsync(Draft(Doc("c.cs"), "c"), CancellationToken.None);
        Clock.Now = new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);
        await Store.WriteAsync(Draft(Doc("a.cs"), "a"), CancellationToken.None);
        Clock.Now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
        await Store.WriteAsync(Draft(Doc("b.cs"), "b"), CancellationToken.None);

        var paths = (await Store.ListAsync(CancellationToken.None)).Select(d => d.Id.Path).ToArray();

        Assert.Equal("a.cs,b.cs,c.cs", string.Join(',', paths));
    }

    [Fact]
    public async Task Concurrent_writes_to_different_ids_all_persist()
    {
        const int count = 64;
        await Parallel.ForEachAsync(Enumerable.Range(0, count), async (i, ct) =>
            await Store.WriteAsync(Draft(Doc($"file-{i}.cs"), $"content {i}"), ct));

        var all = await Store.ListAsync(CancellationToken.None);

        Assert.Equal(count, all.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal($"content {i}", (await Store.ReadAsync(Doc($"file-{i}.cs"), CancellationToken.None))!.Content);
        }
    }

    [Fact]
    public async Task Cancelled_token_is_honoured_by_every_call()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.WriteAsync(Draft(Doc(), "x"), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ReadAsync(Doc(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.DeleteAsync(Doc(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ListAsync(cts.Token));
    }
}
