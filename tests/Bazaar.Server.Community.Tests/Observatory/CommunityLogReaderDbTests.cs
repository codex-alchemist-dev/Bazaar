using Bazaar.Server.Community.Observatory;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Community.Tests.Observatory;

/// <summary>
/// Runs the community log reader against a real PostgreSQL with the server's own <c>community_action</c> migration, so keyset
/// paging, the filters and the no-body rule are checked against the SQL that ships. Skipped only where no database exists.
/// </summary>
public sealed class CommunityLogReaderDbTests
{
    private const string _bodySentinel = "SENTINEL-BODY-5d2b-never-read";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private static async Task<PgTestDatabase> CreateDbAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("community_log", MigrateAsync, ct);
    }

    // Applies migration community/0003 as shipped. The role grants are dropped: the test role setup is not part of this check.
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(Bazaar.Server.Data.Migrations.MigrationRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0003_community_action.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var sql = await new StreamReader(stream).ReadToEndAsync(ct);
        var kept = string.Join('\n', sql.Split('\n').Where(l => !l.StartsWith("REVOKE", StringComparison.Ordinal) && !l.StartsWith("GRANT", StringComparison.Ordinal)));
        await using var cmd = dataSource.CreateCommand(kept);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAsync(NpgsqlDataSource ds, string agent, string platform, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO community_action (agent_id, sponsor_id, platform, action, body, body_sha256, status, created_at) " +
            "VALUES ($1, 'sponsor', $2, 'NewPost', $3, decode(repeat('ab', 32), 'hex'), 'done', $4)");
        cmd.Parameters.AddWithValue(agent);
        cmd.Parameters.AddWithValue(platform);
        cmd.Parameters.AddWithValue(_bodySentinel);
        cmd.Parameters.AddWithValue(at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Paging_across_a_boundary_with_equal_created_at_loses_and_repeats_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        // Rows 1-3 share one timestamp, rows 4-6 another, row 7 is newest; ids rise with insertion.
        foreach (var at in new[] { _t0, _t0, _t0, _t0.AddMinutes(1), _t0.AddMinutes(1), _t0.AddMinutes(1), _t0.AddMinutes(2) })
            await InsertAsync(db.DataSource, "agent-a", "github", at, ct);
        var reader = new NpgsqlCommunityLogReader(db.DataSource);

        var seen = new List<long>();
        CommunityLogPosition? after = null;
        var pages = 0;
        do
        {
            // Page size 2 puts a page boundary inside each run of equal timestamps.
            var page = await reader.ListPageAsync(new CommunityLogFilter(Limit: 2, After: after), ct);
            seen.AddRange(page.Rows.Select(r => r.Id));
            after = page.Next;
            pages++;
            Assert.True(pages <= 10, "paging did not terminate");
        }
        while (after is not null);

        Assert.Equal([7L, 6, 5, 4, 3, 2, 1], seen);
        Assert.Equal(4, pages);
    }

    [Fact]
    public async Task Exact_fit_last_page_has_no_next_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        for (var i = 0; i < 4; i++) await InsertAsync(db.DataSource, "agent-a", "github", _t0.AddSeconds(i), ct);
        var reader = new NpgsqlCommunityLogReader(db.DataSource);

        var first = await reader.ListPageAsync(new CommunityLogFilter(Limit: 2), ct);
        var second = await reader.ListPageAsync(new CommunityLogFilter(Limit: 2, After: first.Next), ct);

        Assert.NotNull(first.Next);
        Assert.Equal(2, second.Rows.Count);
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Filters_apply_to_every_page()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        for (var i = 0; i < 6; i++)
            await InsertAsync(db.DataSource, i % 2 == 0 ? "agent-a" : "agent-b", i < 3 ? "github" : "reddit", _t0.AddSeconds(i), ct);
        var reader = new NpgsqlCommunityLogReader(db.DataSource);

        var byAgent = await reader.ListPageAsync(new CommunityLogFilter(AgentId: "agent-a"), ct);
        var byPlatform = await reader.ListPageAsync(new CommunityLogFilter(Platform: "reddit"), ct);
        var both = await reader.ListPageAsync(new CommunityLogFilter("agent-b", "reddit"), ct);
        var pagedFirst = await reader.ListPageAsync(new CommunityLogFilter(AgentId: "agent-a", Limit: 2), ct);
        var pagedSecond = await reader.ListPageAsync(new CommunityLogFilter(AgentId: "agent-a", Limit: 2, After: pagedFirst.Next), ct);

        Assert.All(byAgent.Rows, r => Assert.Equal("agent-a", r.AgentId));
        Assert.Equal(3, byAgent.Rows.Count);
        Assert.All(byPlatform.Rows, r => Assert.Equal("reddit", r.Platform));
        Assert.Equal(3, byPlatform.Rows.Count);
        Assert.Equal([6L, 4], both.Rows.Select(r => r.Id).ToArray());
        Assert.Equal([5L, 3], pagedFirst.Rows.Select(r => r.Id).ToArray());
        Assert.Equal([1L], pagedSecond.Rows.Select(r => r.Id).ToArray());
        Assert.Null(pagedSecond.Next);
    }

    [Fact]
    public async Task Limit_is_clamped_to_1_through_200_against_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        for (var i = 0; i < 205; i++) await InsertAsync(db.DataSource, "agent-a", "github", _t0.AddSeconds(i), ct);
        var reader = new NpgsqlCommunityLogReader(db.DataSource);

        var tooBig = await reader.ListPageAsync(new CommunityLogFilter(Limit: 100_000), ct);
        var zero = await reader.ListPageAsync(new CommunityLogFilter(Limit: 0), ct);
        var negative = await reader.ListPageAsync(new CommunityLogFilter(Limit: -5), ct);

        Assert.Equal(200, tooBig.Rows.Count);
        Assert.NotNull(tooBig.Next);
        Assert.Single(zero.Rows);
        Assert.Single(negative.Rows);
    }

    [Fact]
    public async Task Rows_carry_the_body_hash_and_never_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        await InsertAsync(db.DataSource, "agent-a", "github", _t0, ct);
        var reader = new NpgsqlCommunityLogReader(db.DataSource);

        var page = await reader.ListPageAsync(new CommunityLogFilter(), ct);

        var row = Assert.Single(page.Rows);
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 32)), row.BodySha256Hex);
        Assert.DoesNotContain(_bodySentinel, System.Text.Json.JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }
}
