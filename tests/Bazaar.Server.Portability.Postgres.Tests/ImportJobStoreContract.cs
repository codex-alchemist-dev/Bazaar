using Bazaar.Server.Portability;
using static Bazaar.Server.Portability.Postgres.Tests.ImportStoreFixtures;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>A store under test and whatever backs it. Disposing the handle drops the backing state.</summary>
public sealed class StoreHandle(IImportJobStore store, Func<ValueTask> dispose) : IAsyncDisposable
{
    public IImportJobStore Store { get; } = store;

    public ValueTask DisposeAsync() => dispose();
}

/// <summary>
/// The behaviour every <see cref="IImportJobStore"/> must show. The in-memory fake and the Postgres store both run these tests,
/// so the two cannot drift apart. A derived class supplies a fresh store per test.
/// </summary>
public abstract class ImportJobStoreContract
{
    /// <summary>Opens an empty store. Each test disposes its handle.</summary>
    protected abstract Task<StoreHandle> OpenAsync(CancellationToken ct);

    [Fact]
    public async Task Created_job_is_staged_and_keeps_its_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;

        await store.CreateJobAsync(NewJob("job-1"), ct);
        var job = await store.GetJobAsync("job-1", ct);

        Assert.NotNull(job);
        Assert.Equal(ImportJobState.Staged, job.State);
        Assert.Equal("owner", job.ActorId);
        Assert.Equal("chat-dump", job.ImporterId);
        Assert.Equal("1.0.0", job.ImporterVersion);
        Assert.Equal(1_000_000, job.MaxUploadBytes);
        Assert.Equal(T0, job.CreatedAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.EndedAt);
        Assert.Null(job.MappingHash);
        var file = Assert.Single(job.SourceManifest);
        Assert.Equal("chat.txt", file.Name);
        Assert.Equal(1234, file.SizeBytes);
        Assert.Equal(Sha('1'), file.Sha256);
    }

    [Fact]
    public async Task Unknown_job_reads_as_null_with_no_batches_or_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;

        Assert.Null(await store.GetJobAsync("missing", ct));
        Assert.Empty(await store.ListBatchesAsync("missing", ct));
        Assert.Empty(await store.ReadCommittedSourceKeysAsync("missing", ct));
    }

    [Fact]
    public async Task Duplicate_job_id_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await store.CreateJobAsync(NewJob("job-1"), ct);

        await Assert.ThrowsAsync<ImportStoreConflictException>(async () => await store.CreateJobAsync(NewJob("job-1"), ct));
    }

    [Fact]
    public async Task Dry_run_hashes_are_recorded_until_commit_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await store.CreateJobAsync(NewJob("job-1"), ct);

        Assert.True(await store.RecordDryRunAsync("job-1", Sha('a'), Sha('b'), ct));
        var staged = await store.GetJobAsync("job-1", ct);
        Assert.Equal(Sha('a'), staged!.MappingHash);
        Assert.Equal(Sha('b'), staged.DryRunHash);

        Assert.True(await store.TransitionAsync("job-1", ImportJobState.DryRun, T0.AddMinutes(1), null, ct));
        Assert.True(await store.RecordDryRunAsync("job-1", Sha('c'), Sha('d'), ct), "a dry run may be repeated before commit");
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Committing, T0.AddMinutes(2), null, ct));
        Assert.False(await store.RecordDryRunAsync("job-1", Sha('e'), Sha('f'), ct), "no dry run may be recorded once committing");
        Assert.Equal(Sha('c'), (await store.GetJobAsync("job-1", ct))!.MappingHash);
    }

    [Fact]
    public async Task Dry_run_on_a_missing_job_is_not_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);

        Assert.False(await handle.Store.RecordDryRunAsync("missing", Sha('a'), Sha('b'), ct));
    }

    [Fact]
    public async Task Transitions_follow_the_lifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await store.CreateJobAsync(NewJob("job-1"), ct);

        Assert.False(await store.TransitionAsync("job-1", ImportJobState.Committing, T0, null, ct), "a staged job cannot skip the dry run");
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.DryRun, T0, null, ct));
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Committing, T0, null, ct));
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Paused, T0, null, ct));
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Committing, T0, null, ct));
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Verifying, T0, null, ct));
        Assert.True(await store.TransitionAsync("job-1", ImportJobState.Verified, T0, null, ct));
        Assert.False(await store.TransitionAsync("job-1", ImportJobState.Committing, T0, null, ct), "a verified job is terminal");
        Assert.Equal(ImportJobState.Verified, (await store.GetJobAsync("job-1", ct))!.State);
        Assert.False(await store.TransitionAsync("missing", ImportJobState.DryRun, T0, null, ct));
    }

    [Fact]
    public async Task Start_is_set_once_and_a_terminal_state_sets_the_end_and_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await store.CreateJobAsync(NewJob("job-1"), ct);
        await store.TransitionAsync("job-1", ImportJobState.DryRun, T0, null, ct);

        await store.TransitionAsync("job-1", ImportJobState.Committing, T0.AddMinutes(1), null, ct);
        await store.TransitionAsync("job-1", ImportJobState.Paused, T0.AddMinutes(2), null, ct);
        await store.TransitionAsync("job-1", ImportJobState.Committing, T0.AddMinutes(5), null, ct);
        Assert.Equal(T0.AddMinutes(1), (await store.GetJobAsync("job-1", ct))!.StartedAt);

        await store.TransitionAsync("job-1", ImportJobState.Failed, T0.AddMinutes(6), "disk full", ct);
        var failed = await store.GetJobAsync("job-1", ct);
        Assert.Equal(ImportJobState.Failed, failed!.State);
        Assert.Equal(T0.AddMinutes(1), failed.StartedAt);
        Assert.Equal(T0.AddMinutes(6), failed.EndedAt);
        Assert.Equal("disk full", failed.FailureReason);
    }

    [Fact]
    public async Task Batch_is_recorded_with_its_counts_checkpoint_and_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await CommittingJobAsync(store, "job-1", ct);

        var items = new[]
        {
            Item("job-1", "a"),
            Item("job-1", "b"),
            Item("job-1", "c", ImportTargetKind.VaultFile),
        };
        await store.RecordBatchAsync(Batch("job-1", 1, T0.AddMinutes(10), items), items, ct);

        var job = await store.GetJobAsync("job-1", ct);
        Assert.Equal(T0.AddMinutes(10), job!.LastProgressAt);
        var batch = Assert.Single(await store.ListBatchesAsync("job-1", ct));
        Assert.Equal(1, batch.BatchNumber);
        Assert.Equal(3, batch.ItemCount);
        Assert.Equal(2, batch.ItemCounts[ImportTargetKind.GcMessage]);
        Assert.Equal(1, batch.ItemCounts[ImportTargetKind.VaultFile]);
        Assert.Equal("a", batch.FirstSourceKey);
        Assert.Equal("c", batch.LastSourceKey);
        Assert.Equal(Sha('c'), batch.BatchHash);
        Assert.Equal(T0.AddMinutes(10), batch.CommittedAt);
        AssertKeys(await store.ReadCommittedSourceKeysAsync("job-1", ct), "a", "b", "c");
    }

    [Fact]
    public async Task Batch_needs_a_committing_job_and_writes_nothing_otherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await store.CreateJobAsync(NewJob("job-1"), ct);
        var items = new[] { Item("job-1", "a") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.RecordBatchAsync(Batch("job-1", 1, T0, items), items, ct));

        Assert.Empty(await store.ListBatchesAsync("job-1", ct));
        Assert.Empty(await store.ReadCommittedSourceKeysAsync("job-1", ct));
    }

    [Fact]
    public async Task Repeated_batch_number_is_refused_and_keeps_the_first_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await CommittingJobAsync(store, "job-1", ct);
        var first = new[] { Item("job-1", "a"), Item("job-1", "b") };
        await store.RecordBatchAsync(Batch("job-1", 1, T0.AddMinutes(1), first), first, ct);
        var again = new[] { Item("job-1", "c"), Item("job-1", "d") };

        await Assert.ThrowsAsync<ImportStoreConflictException>(
            async () => await store.RecordBatchAsync(Batch("job-1", 1, T0.AddMinutes(2), again), again, ct));

        Assert.Single(await store.ListBatchesAsync("job-1", ct));
        AssertKeys(await store.ReadCommittedSourceKeysAsync("job-1", ct), "a", "b");
    }

    [Fact]
    public async Task A_batch_with_one_recorded_item_is_refused_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await CommittingJobAsync(store, "job-1", ct);
        var first = new[] { Item("job-1", "a") };
        await store.RecordBatchAsync(Batch("job-1", 1, T0.AddMinutes(1), first), first, ct);
        var clash = new[] { Item("job-1", "z"), Item("job-1", "a") };

        await Assert.ThrowsAsync<ImportStoreConflictException>(
            async () => await store.RecordBatchAsync(Batch("job-1", 2, T0.AddMinutes(2), clash), clash, ct));

        Assert.Single(await store.ListBatchesAsync("job-1", ct));
        AssertKeys(await store.ReadCommittedSourceKeysAsync("job-1", ct), "a");
    }

    [Fact]
    public async Task Changed_source_adds_a_version_and_the_latest_item_is_the_newest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await CommittingJobAsync(store, "job-1", ct);
        var first = new[] { Item("job-1", "a", version: "v1", contentHash: Sha('1')) };
        await store.RecordBatchAsync(Batch("job-1", 1, T0.AddMinutes(1), first), first, ct);
        await store.TransitionAsync("job-1", ImportJobState.Paused, T0.AddMinutes(2), null, ct);
        await store.TransitionAsync("job-1", ImportJobState.Committing, T0.AddMinutes(3), null, ct);
        var changed = new[] { Item("job-1", "a", version: "v2", contentHash: Sha('2')) };
        await store.RecordBatchAsync(Batch("job-1", 2, T0.AddMinutes(4), changed), changed, ct);

        var latest = await store.GetLatestItemAsync("chat-dump", "a", ct);
        Assert.NotNull(latest);
        Assert.Equal("v2", latest.TargetVersion);
        Assert.Equal(Sha('2'), latest.ContentHash);
        Assert.Equal("job-1", latest.JobId);
        Assert.Equal(ImportTargetKind.GcMessage, latest.TargetKind);
        Assert.Null(await store.GetLatestItemAsync("chat-dump", "never-seen", ct));
        Assert.Null(await store.GetLatestItemAsync("other-importer", "a", ct));
    }

    [Fact]
    public async Task Committed_keys_are_scoped_to_their_own_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var handle = await OpenAsync(ct);
        var store = handle.Store;
        await CommittingJobAsync(store, "job-a", ct);
        await CommittingJobAsync(store, "job-b", ct);
        var a = new[] { Item("job-a", "a") };
        var b = new[] { Item("job-b", "b") };
        await store.RecordBatchAsync(Batch("job-a", 1, T0.AddMinutes(1), a), a, ct);
        await store.RecordBatchAsync(Batch("job-b", 1, T0.AddMinutes(1), b), b, ct);

        AssertKeys(await store.ReadCommittedSourceKeysAsync("job-a", ct), "a");
        AssertKeys(await store.ReadCommittedSourceKeysAsync("job-b", ct), "b");
    }

    /// <summary>Asserts the keys hold exactly the expected values, in any order.</summary>
    protected static void AssertKeys(IEnumerable<string> actual, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    /// <summary>Creates a job and walks it to committing, the state a batch needs.</summary>
    protected static async Task CommittingJobAsync(IImportJobStore store, string jobId, CancellationToken ct)
    {
        await store.CreateJobAsync(NewJob(jobId), ct);
        await store.TransitionAsync(jobId, ImportJobState.DryRun, T0, null, ct);
        await store.TransitionAsync(jobId, ImportJobState.Committing, T0, null, ct);
    }
}
