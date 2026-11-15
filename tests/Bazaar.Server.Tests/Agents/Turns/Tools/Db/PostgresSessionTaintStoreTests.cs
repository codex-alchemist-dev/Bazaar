using System.Collections.Immutable;
using Bazaar.Server.Agents.Turns.Tools.Db;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Security.Approvals.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns.Tools.Db;

/// <summary>
/// PostgresSessionTaintStore against a real PostgreSQL: empty session, latest-row-wins, the no-decrease trigger and the
/// append-only triggers. Reuses the approval test database helper, which migrates the whole assembly with the real runner.
/// </summary>
public sealed class PostgresSessionTaintStoreTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _session = Guid.Parse("11111111-2222-3333-4444-666666666666");
    private static readonly Guid _source = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");

    private static SessionTaint Taint(TaintState state, int sourceCount) => new(
        state,
        Enumerable.Range(0, sourceCount)
            .Select(i => new TaintSource(_source, ContentTrust.External, TaintState.Hard))
            .ToImmutableArray());

    [Fact]
    public async Task Session_with_no_rows_loads_the_clean_value()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresSessionTaintStore(db.DataSource);

        var taint = await store.LoadAsync(_session, Ct);

        Assert.Equal(SessionTaint.Clean.State, taint.State);
        Assert.Empty(taint.Sources);
    }

    [Fact]
    public async Task Saved_taint_loads_back_as_the_latest_row()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresSessionTaintStore(db.DataSource);

        await store.SaveAsync(_session, Taint(TaintState.Soft, 1), Ct);
        var latest = Taint(TaintState.Hard, 2);
        await store.SaveAsync(_session, latest, Ct);

        var loaded = await store.LoadAsync(_session, Ct);

        Assert.Equal(latest.State, loaded.State);
        Assert.Equal(latest.Sources, loaded.Sources);
    }

    [Fact]
    public async Task Saving_a_lower_level_is_refused_by_the_database()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresSessionTaintStore(db.DataSource);
        await store.SaveAsync(_session, Taint(TaintState.Hard, 1), Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.SaveAsync(_session, Taint(TaintState.Soft, 1), Ct));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Updating_a_taint_row_is_refused_by_the_append_only_trigger()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresSessionTaintStore(db.DataSource);
        await store.SaveAsync(_session, Taint(TaintState.Soft, 1), Ct);

        await using var cmd = db.DataSource.CreateCommand("UPDATE session_taint SET state = 'Clean'");
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
    }
}
