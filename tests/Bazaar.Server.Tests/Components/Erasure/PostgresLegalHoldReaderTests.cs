using Bazaar.Server.Erasure;
using Bazaar.Server.Erasure.Abstractions;
using Bazaar.Server.Components.Erasure;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Components.Erasure;

/// <summary>Legal hold storage on a real PostgreSQL: the active view, the person read and the append-only refusals.</summary>
public sealed class PostgresLegalHoldReaderTests(ErasureDb db) : IClassFixture<ErasureDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_person_hold_blocks_only_that_person()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var held = Guid.NewGuid();
        var other = Guid.NewGuid();
        await InsertAsync(Guid.NewGuid(), "created", "person", held.ToString("D"));

        var reader = new PostgresLegalHoldReader(db.DataSource);

        Assert.True(await reader.IsPersonHeldAsync(held, Ct));
        Assert.False(await reader.IsPersonHeldAsync(other, Ct));
    }

    [Fact]
    public async Task A_released_hold_is_no_longer_active()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var person = Guid.NewGuid();
        var hold = Guid.NewGuid();
        await InsertAsync(hold, "created", "person", person.ToString("D"));
        await InsertAsync(hold, "released", "person", person.ToString("D"));

        var reader = new PostgresLegalHoldReader(db.DataSource);

        Assert.False(await reader.IsPersonHeldAsync(person, Ct));
    }

    [Fact]
    public async Task Gc_company_and_category_holds_are_not_enforced_for_a_person_yet()
    {
        // Current behaviour, pending the owner's decision on how these scopes cover a person (OPEN).
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var person = Guid.NewGuid();
        await InsertAsync(Guid.NewGuid(), "created", "gc", person.ToString("D"));
        await InsertAsync(Guid.NewGuid(), "created", "company", person.ToString("D"));
        await InsertAsync(Guid.NewGuid(), "created", "category", person.ToString("D"));

        var reader = new PostgresLegalHoldReader(db.DataSource);

        Assert.False(await reader.IsPersonHeldAsync(person, Ct));
    }

    [Fact]
    public async Task The_hold_table_refuses_update_delete_and_truncate()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await InsertAsync(Guid.NewGuid(), "created", "person", Guid.NewGuid().ToString("D"));

        await AssertRefusedAsync("UPDATE legal_hold_event SET reason = 'changed'");
        await AssertRefusedAsync("DELETE FROM legal_hold_event");
        await AssertRefusedAsync("TRUNCATE legal_hold_event");
    }

    [Fact]
    public async Task The_app_role_may_insert_and_select_but_not_update_or_delete()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT has_table_privilege('bazaar_app', 'legal_hold_event', 'INSERT'), " +
            "has_table_privilege('bazaar_app', 'legal_hold_event', 'SELECT'), " +
            "has_table_privilege('bazaar_app', 'legal_hold_event', 'UPDATE'), " +
            "has_table_privilege('bazaar_app', 'legal_hold_event', 'DELETE'), " +
            "has_table_privilege('bazaar_app', 'active_legal_hold', 'SELECT')");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    private async Task InsertAsync(Guid holdId, string action, string scopeKind, string scopeValue)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO legal_hold_event (hold_id, action, scope_kind, scope_value, reason, actor) VALUES ($1, $2, $3, $4, 'test', 'test')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = holdId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = action });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scopeKind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scopeValue });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task AssertRefusedAsync(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
    }
}
