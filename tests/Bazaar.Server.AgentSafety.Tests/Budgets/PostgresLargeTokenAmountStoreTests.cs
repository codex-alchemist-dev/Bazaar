using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Security.Budgets;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Server.AgentSafety.Tests.Budgets.LargeTierTestSupport;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// The large tier of doc 48, DECIDED 11.4 against a real PostgreSQL: staged writes, sealing, chunked reads with the hash
/// check, deletion and the sweeper, at the real 1 MiB chunk size.
/// </summary>
public sealed class PostgresLargeTokenAmountStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Five_million_digits_round_trip_add_to_themselves_in_bounded_memory_and_delete()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var pool = new TokenAmountBufferPool();
        var store = new PostgresLargeTokenAmountStore(db.DataSource, codec: new TokenAmountChunkCodec(pool));
        var codec = store.Codec;
        var random = new Random(20261119);
        var text = RandomDigits(random, 5_000_000) + "." + RandomDigits(random, 3_001, nonZeroFirst: false, nonZeroLast: true);

        // Write: 2,500,000 integer bytes are three chunks (two full 1 MiB chunks and the outermost), the fraction one.
        Guid id;
        await using (var staged = await store.BeginStagingAsync(Ct))
        {
            var written = await codec.WriteDecimalAsync(text.AsMemory(), staged, Ct);
            Assert.Equal(new TokenAmountDigitCounts(5_000_000, 3_001), written);
            await store.SealAsync(staged.Id, Ct);
            staged.Keep();
            id = staged.Id;
        }

        Assert.Equal((0L, 1L, 4L), await CountsAsync(db.DataSource, Ct));

        // Read back: the hash is checked over every chunk, and the stored amount equals the source text.
        var stored = (await store.OpenAsync(id, Ct))!;
        await store.VerifyAsync(id, Ct);
        var reference = new InMemoryChunkedTokenAmount();
        await codec.WriteDecimalAsync(text.AsMemory(), reference, Ct);
        Assert.Equal(reference.Sha256.ToArray(), stored.Sha256.ToArray());
        Assert.Equal(0, await codec.CompareAsync(stored, reference, Ct));

        // Add it to itself through the streamed adder, into a new staged amount, with the pool counting every chunk buffer.
        pool.ResetPeak();
        await using var doubled = await store.BeginStagingAsync(Ct);
        var sumDigits = await codec.AddAsync(stored, (await store.OpenAsync(id, Ct))!, doubled, Ct);
        Assert.InRange(pool.Peak, 1, 4);
        Assert.Equal(0, pool.Outstanding);

        var expected = new InMemoryChunkedTokenAmount();
        await codec.WriteDecimalAsync(Double(text).AsMemory(), expected, Ct);
        Assert.Equal(expected.Digits, sumDigits);
        Assert.Equal(expected.Sha256.ToArray(), doubled.Sha256);
        Assert.Equal(0, await codec.CompareAsync(doubled.OpenReader(), expected, Ct));
        Assert.True(await codec.CompareAsync(doubled.OpenReader(), stored, Ct) > 0);

        // Materialising is refused above the in-memory maximum without reading a chunk.
        Assert.Null(await store.TryReadAsync(id, TokenBudgetOptions.DefaultInMemoryMaxDigits, Ct));

        // Delete: the header and every chunk go; the unsealed sum goes when its writer is disposed.
        Assert.True(await store.DeleteAsync(id, Ct));
        Assert.Null(await store.OpenAsync(id, Ct));
        await doubled.DisposeAsync();
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_staged_amount_is_unreadable_until_sealed_and_is_swept_after_the_staging_age()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var time = new FakeTimeProvider(_t0);
        var store = new PostgresLargeTokenAmountStore(db.DataSource, time);

        var staged = await store.StageAsync(TokenAmount.Parse("12345678901234567890.5"), Ct);
        var sealedAmount = await store.StageAsync(TokenAmount.Parse("42"), Ct);
        await store.SealAsync(sealedAmount.Id, Ct);
        sealedAmount.Keep();

        Assert.Null(await store.OpenAsync(staged.Id, Ct));
        Assert.Equal(0, await store.SweepStagedAsync(_t0, Ct));
        Assert.Equal(1, await store.SweepStagedAsync(_t0.AddHours(24).AddTicks(10), Ct));
        Assert.Equal((0L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal(TokenAmount.Parse("42"), await store.TryReadAsync(sealedAmount.Id, 100, Ct));

        // A swept amount cannot be sealed any more, and its writer's cleanup finds nothing left to delete.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SealAsync(staged.Id, Ct));
        await staged.DisposeAsync();
        await sealedAmount.DisposeAsync();
        Assert.Equal((0L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_writer_whose_header_was_swept_fails_instead_of_leaving_orphan_chunks()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var time = new FakeTimeProvider(_t0);
        // One chunk per transaction, so the first chunk is committed: a sweep waits for an open batch that holds the header.
        var store = new PostgresLargeTokenAmountStore(db.DataSource, time, chunksPerTransaction: 1);

        await using var staged = await store.BeginStagingAsync(Ct);
        await staged.WriteChunkAsync(TokenAmountPart.IntegerPart, 0, new byte[] { 12 }, Ct);
        Assert.Equal(1, await store.SweepStagedAsync(_t0.AddDays(2), Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await staged.WriteChunkAsync(TokenAmountPart.IntegerPart, 1, new byte[] { 34 }, Ct));
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_tampered_chunk_fails_the_integrity_check()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresLargeTokenAmountStore(db.DataSource);
        await using var staged = await store.StageAsync(TokenAmount.Parse("98765432109876543210.123"), Ct);
        await store.SealAsync(staged.Id, Ct);
        staged.Keep();

        // One digit pair changed, still a valid base-100 byte: only the hash can tell.
        await using (var tamper = db.DataSource.CreateCommand(
            "UPDATE token_amount_large_chunk SET digits = set_byte(digits, 1, (get_byte(digits, 1) + 1) % 100) WHERE amount_id = $1 AND part = 0"))
        {
            tamper.Parameters.AddWithValue(staged.Id);
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync(Ct));
        }

        await Assert.ThrowsAsync<TokenAmountIntegrityException>(() => store.VerifyAsync(staged.Id, Ct));
        await Assert.ThrowsAsync<TokenAmountIntegrityException>(() => store.TryReadAsync(staged.Id, 1_000, Ct));
    }

    [Fact]
    public async Task The_sweeper_job_runs_on_its_interval_and_removes_only_old_staged_amounts()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var time = new FakeTimeProvider(_t0);
        var store = new PostgresLargeTokenAmountStore(db.DataSource, time);
        var options = new TokenBudgetOptions { LargeAmountStagingHours = 1 };
        await using var pending = await store.StageAsync(TokenAmount.Parse("7"), Ct);
        using var sweeper = new LargeTokenAmountSweeper(store, options, time, NullLogger<LargeTokenAmountSweeper>.Instance);
        var swept = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        sweeper.Swept += removed =>
        {
            if (removed > 0)
            {
                swept.TrySetResult(removed);
            }
        };

        await sweeper.StartAsync(Ct);
        time.Advance(LargeTokenAmountSweeper.Interval);
        time.Advance(LargeTokenAmountSweeper.Interval);

        Assert.Equal(1, await swept.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        await sweeper.StopAsync(Ct);
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }
}
