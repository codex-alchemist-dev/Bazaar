namespace Bazaar.Server.TalentPool.Tests;

/// <summary>The rules every <see cref="ITalentTemplateStore"/> keeps. Run against the in-memory fake (always) and Postgres (needs a database).</summary>
public abstract class TalentTemplateStoreContract
{
    /// <summary>Opens a fresh, empty store. Dispose the result to release it.</summary>
    internal abstract Task<(ITalentTemplateStore Store, IAsyncDisposable Lease)> OpenAsync(CancellationToken ct);

    [Fact]
    public async Task A_new_template_is_version_one_and_quarantined()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;
        var id = Guid.NewGuid();
        var soul = Guid.NewGuid();

        var added = await store.AddVersionAsync(id, Samples.Validated(), soul, "agent:alice", ct);

        Assert.True(added.Created);
        Assert.Equal(1, added.Template.Version);
        Assert.Equal(TalentTemplateStatus.Quarantined, added.Template.Status);
        Assert.Equal(soul, added.Template.SourceSoulId);
        var read = await store.GetAsync(id, 1, ct);
        Assert.NotNull(read);
        Assert.Equal(TalentTemplateStatus.Quarantined, read.Status);
        Assert.Equal("You review pull requests carefully.", read.Manifest.Prompt);
        Assert.Equal(["read_file", "run_tests"], read.Manifest.ToolAllowlist);
        Assert.Equal(["haiku-5-5"], read.Manifest.ModelWhitelist);
    }

    [Fact]
    public async Task Different_content_gets_the_next_version_and_the_same_content_is_not_stored_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;
        var id = Guid.NewGuid();

        var v1 = await store.AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);
        var v2 = await store.AddVersionAsync(id, Samples.Validated(Samples.Manifest(prompt: "Changed.")), null, "agent:alice", ct);
        var again = await store.AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);

        Assert.Equal(1, v1.Template.Version);
        Assert.Equal(2, v2.Template.Version);
        Assert.False(again.Created);
        Assert.Equal(1, again.Template.Version);
        Assert.Equal([1, 2], (await store.ListVersionsAsync(id, ct)).Select(t => t.Version));
    }

    [Fact]
    public async Task Templates_do_not_share_version_numbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;

        var a = await store.AddVersionAsync(Guid.NewGuid(), Samples.Validated(), null, "agent:alice", ct);
        var b = await store.AddVersionAsync(Guid.NewGuid(), Samples.Validated(), null, "agent:bob", ct);

        Assert.Equal(1, a.Template.Version);
        Assert.Equal(1, b.Template.Version);
        Assert.True(b.Created);
    }

    [Fact]
    public async Task A_missing_version_reads_as_null_and_an_unknown_template_lists_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;

        Assert.Null(await store.GetAsync(Guid.NewGuid(), 1, ct));
        Assert.Empty(await store.ListVersionsAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task A_quarantined_version_resolves_once_and_the_result_is_final()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;
        var id = Guid.NewGuid();
        await store.AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);

        Assert.True(await store.TryResolveAsync(id, 1, TalentTemplateStatus.Hired, ct));
        Assert.False(await store.TryResolveAsync(id, 1, TalentTemplateStatus.Rejected, ct));
        Assert.Equal(TalentTemplateStatus.Hired, (await store.GetAsync(id, 1, ct))!.Status);
        Assert.False(await store.TryResolveAsync(id, 9, TalentTemplateStatus.Hired, ct));
    }

    [Fact]
    public async Task A_version_cannot_be_moved_back_into_quarantine()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;
        var id = Guid.NewGuid();
        await store.AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryResolveAsync(id, 1, TalentTemplateStatus.Quarantined, ct));
    }

    [Fact]
    public async Task The_version_diff_names_what_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, lease) = await OpenAsync(ct);
        await using var _ = lease;
        var id = Guid.NewGuid();
        await store.AddVersionAsync(id, Samples.Validated(Samples.Manifest(tools: ["read_file", "run_tests"], models: ["haiku-5-5"])), null, "agent:alice", ct);
        await store.AddVersionAsync(id, Samples.Validated(Samples.Manifest(prompt: "New prompt.", tools: ["read_file", "write_file"], models: ["haiku-5-5", "sonnet-5-5"])), null, "agent:alice", ct);

        var diff = await store.DiffAsync(id, 1, 2, ct);

        Assert.NotNull(diff);
        Assert.True(diff.PromptChanged);
        Assert.Equal(["write_file"], diff.ToolsAdded);
        Assert.Equal(["run_tests"], diff.ToolsRemoved);
        Assert.Equal(["sonnet-5-5"], diff.ModelsAdded);
        Assert.Empty(diff.ModelsRemoved);
        Assert.False(diff.IsEmpty);
        Assert.True((await store.DiffAsync(id, 1, 1, ct))!.IsEmpty);
        Assert.Null(await store.DiffAsync(id, 1, 7, ct));
    }
}

public sealed class InMemoryTalentTemplateStoreTests : TalentTemplateStoreContract
{
    internal override Task<(ITalentTemplateStore Store, IAsyncDisposable Lease)> OpenAsync(CancellationToken ct) =>
        Task.FromResult<(ITalentTemplateStore, IAsyncDisposable)>((new InMemoryTalentTemplateStore(), NoLease.Instance));

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
