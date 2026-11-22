using System.Text;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Security.Budgets;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Bazaar.Server.AgentSafety.Tests.Budgets.LargeTierTestSupport;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// The digit-stream intake of doc 48, DECIDED 11.4 ("Transport above the frame size") against a real PostgreSQL, at the real
/// 1 MiB chunk size: a stream of several chunks is sealed with its owner row in bounded memory, and malformed, over-guard,
/// inline-sized and cancelled streams leave nothing sealed.
/// </summary>
public sealed class PostgresTokenAmountIntakeTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task A_stream_of_several_chunks_is_sealed_with_its_owner_row_in_bounded_memory()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var pool = new TokenAmountBufferPool();
        var store = new PostgresLargeTokenAmountStore(db.DataSource, new FakeTimeProvider(_t0), new TokenAmountChunkCodec(pool), chunksPerTransaction: 2);
        var random = new Random(20261119);

        // Leading zeros and trailing zeros arrive too; the canonical amount drops them.
        var digits = RandomDigits(random, 5_000_000) + "." + RandomDigits(random, 3_001, nonZeroFirst: false, nonZeroLast: true);
        var text = "000" + digits + "0000";

        pool.ResetPeak();
        var (id, counts) = await store.IntakeAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), TokenAmountBounds.Unlimited, _owner, Ct);

        // Three integer chunks and one fraction chunk, about 5 MB staged twice, with a read buffer and three chunk buffers at most.
        Assert.Equal(new TokenAmountDigitCounts(5_000_000, 3_001), counts);
        Assert.InRange(pool.Peak, 1, 3);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal((0L, 1L, 4L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal("sealed", await TextAsync(db.DataSource, $"SELECT state FROM token_amount_large WHERE id = '{id:D}'"));
        Assert.Equal(_owner.ToString("D"), await TextAsync(db.DataSource, $"SELECT created_by::text FROM token_amount_intake WHERE amount_id = '{id:D}'"));

        // The stored amount is the one the text names, by hash and by streamed comparison.
        await store.VerifyAsync(id, Ct);
        var reference = new InMemoryChunkedTokenAmount();
        await store.Codec.WriteDecimalAsync(digits.AsMemory(), reference, Ct);
        var stored = (await store.OpenAsync(id, Ct))!;
        Assert.Equal(reference.Sha256.ToArray(), stored.Sha256.ToArray());
        Assert.Equal(0, await store.Codec.CompareAsync(stored, reference, Ct));
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("non-digit")]
    [InlineData("inline")]
    [InlineData("zero")]
    public async Task A_refused_stream_leaves_nothing_sealed_and_nothing_for_the_sweeper(string kind)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresLargeTokenAmountStore(db.DataSource, new FakeTimeProvider(_t0));
        var (text, guard, failure) = kind switch
        {
            // 300,000 digits against a guard of 200,000: refused part way through the body.
            "guard" => ("7" + new string('1', 299_999), new TokenAmountBounds(200_000, 10), TokenAmountIntakeFailure.OverGuard),
            "non-digit" => (new string('9', 150_000) + "," + new string('9', 150_000), TokenAmountBounds.Unlimited, TokenAmountIntakeFailure.Malformed),
            "inline" => ("1" + new string('0', 131_071) + "." + new string('5', 16_383), TokenAmountBounds.Unlimited, TokenAmountIntakeFailure.FitsInline),
            _ => (new string('0', 200_000), TokenAmountBounds.Unlimited, TokenAmountIntakeFailure.FitsInline),
        };

        var ex = await Assert.ThrowsAsync<TokenAmountIntakeException>(() =>
            store.IntakeAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), guard, _owner, Ct));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state = 'sealed'"));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_intake"));
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_cancelled_stream_leaves_only_writing_rows_which_the_sweeper_removes()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var time = new FakeTimeProvider(_t0);
        var store = new PostgresLargeTokenAmountStore(db.DataSource, time, chunksPerTransaction: 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // 6 million digits arrive in 64 KiB reads; the request is cancelled after about 3 MiB, once scratch chunks were committed.
        var body = new CancellingStream(Encoding.ASCII.GetBytes("9" + new string('8', 5_999_999)), cancelAfterBytes: 3_000_000, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IntakeAsync(body, TokenAmountBounds.Unlimited, _owner, cts.Token));

        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state <> 'writing'"));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_intake"));

        time.Advance(TimeSpan.FromHours(TokenBudgetOptions.DefaultLargeAmountStagingHours + 1));
        var sweeper = new LargeTokenAmountSweeper(store, new TokenBudgetOptions(), time, NullLogger<LargeTokenAmountSweeper>.Instance);
        await sweeper.SweepOnceAsync(Ct);

        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    [Fact]
    public async Task A_sweeper_removes_what_an_interrupted_intake_could_not_delete()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var time = new FakeTimeProvider(_t0);
        var store = new PostgresLargeTokenAmountStore(db.DataSource, time, chunksPerTransaction: 1);

        // A process that dies mid-stream never runs its cleanup: its staged rows stay 'writing', with committed chunks.
        var staged = await store.BeginStagingAsync(Ct);
        await staged.WriteChunkAsync(TokenAmountPart.IntegerPart, 0, new byte[] { 12, 34 }, Ct);
        await staged.FlushAsync(Ct);
        staged.Keep();
        await staged.DisposeAsync();
        Assert.Equal((0L, 1L, 1L), await CountsAsync(db.DataSource, Ct));
        Assert.Equal(0L, await ScalarAsync(db.DataSource, "SELECT count(*) FROM token_amount_large WHERE state <> 'writing'"));

        time.Advance(TimeSpan.FromHours(TokenBudgetOptions.DefaultLargeAmountStagingHours + 1));
        var sweeper = new LargeTokenAmountSweeper(store, new TokenBudgetOptions(), time, NullLogger<LargeTokenAmountSweeper>.Instance);

        Assert.Equal(1, await sweeper.SweepOnceAsync(Ct));
        Assert.Equal((0L, 0L, 0L), await CountsAsync(db.DataSource, Ct));
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string?> TextAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (string?)await cmd.ExecuteScalarAsync(Ct);
    }

    /// <summary>A request body that cancels its request once a number of bytes has been read.</summary>
    private sealed class CancellingStream(byte[] data, int cancelAfterBytes, CancellationTokenSource cts) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= cancelAfterBytes)
            {
                await cts.CancelAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
