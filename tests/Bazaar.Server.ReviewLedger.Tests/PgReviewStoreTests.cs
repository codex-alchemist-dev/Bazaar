using Bazaar.Server.ReviewLedger;
using Bazaar.Server.ReviewLedger.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.ReviewLedger.Tests;

/// <summary>A throwaway database with the <c>review</c> table, columns as in common/0013.</summary>
public sealed class ReviewDb : IAsyncLifetime
{
    private PgSource? _source;
    private string _database = "";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _source = await PgSource.GetSharedAsync();
        _database = "review_ledger_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_source.AdminConnectionString))
        {
            await using var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\"");
            await create.ExecuteNonQueryAsync();
        }

        DataSource = NpgsqlDataSource.Create(_source.ConnectionStringFor(_database));
        await using var table = DataSource.CreateCommand("""
            CREATE TABLE review (
                id uuid PRIMARY KEY, pr_ref text NOT NULL, card_id uuid, reviewer_agent_id text NOT NULL, author_agent_id text NOT NULL,
                reviewer_family text, author_family text, blinded boolean NOT NULL, tag text NOT NULL,
                overall smallint NOT NULL CHECK (overall BETWEEN 1 AND 5), rubric jsonb, rationale_ref text,
                supersedes_id uuid REFERENCES review (id), created_at timestamptz NOT NULL DEFAULT now());
            """);
        await table.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(_source!.AdminConnectionString);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string pr)
    {
        await using var cmd = DataSource.CreateCommand("SELECT count(*) FROM review WHERE pr_ref = @pr");
        cmd.Parameters.AddWithValue("pr", pr);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

public class PgReviewStoreTests(ReviewDb db) : IClassFixture<ReviewDb>
{
    private static ReviewRecord Review(string reviewer, string pr, Guid? supersedes = null) =>
        new(pr, reviewer, "author", "csharp", 4, true, RubricJson: "{\"correctness\":4}", RationaleRef: "vault/notes/r.md@abc", SupersedesId: supersedes);

    [Fact]
    public async Task Stores_the_review_and_a_superseding_one()
    {
        var recorder = new ReviewRecorder(new PgReviewStore(db.DataSource), new ReviewCaptureSettings(), TimeProvider.System);
        var first = await recorder.RecordAsync(Review("r1", "org/r#10"), TestContext.Current.CancellationToken);
        var second = await recorder.RecordAsync(Review("r1", "org/r#10", first.ReviewId), TestContext.Current.CancellationToken);

        Assert.True(first.Recorded);
        Assert.True(second.Recorded);
        Assert.Equal(2, await db.CountAsync("org/r#10"));
    }

    [Fact]
    public async Task Concurrent_reviewers_cannot_pass_the_cap()
    {
        var recorder = new ReviewRecorder(new PgReviewStore(db.DataSource), new ReviewCaptureSettings(), TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => recorder.RecordAsync(Review("r" + i, "org/r#11"), TestContext.Current.CancellationToken)));

        Assert.Equal(2, results.Count(r => r.Recorded));
        Assert.Equal(4, results.Count(r => r.Refusal == ReviewRefusal.TooManyReviewers));
        Assert.Equal(2, await db.CountAsync("org/r#11"));
    }
}
