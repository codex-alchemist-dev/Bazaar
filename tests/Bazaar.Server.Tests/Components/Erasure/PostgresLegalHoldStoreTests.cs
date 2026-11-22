using Bazaar.Erasure;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Components.Erasure;

/// <summary>Captures audit appends in memory.</summary>
internal sealed class HoldAuditLog : ITransactionalAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }

    public Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>Hold create and release on a real PostgreSQL: the active view, the audit row, and one winner among racing releases.</summary>
public sealed class PostgresLegalHoldStoreTests(ErasureDb db) : IClassFixture<ErasureDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_then_release_moves_a_hold_through_the_active_view_and_audits_both()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var audit = new HoldAuditLog();
        var store = new PostgresLegalHoldStore(db.DataSource, audit);
        var person = Guid.NewGuid().ToString("D");
        var hold = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await store.CreateAsync(hold, LegalHoldScopeKind.Person, person, "case 42", "owner-1", "device-1", at, Ct);

        var active = Assert.Single(await store.ListActiveAsync(Ct), h => h.HoldId == hold);
        Assert.Equal(LegalHoldScopeKind.Person, active.ScopeKind);
        Assert.Equal(person, active.ScopeValue);
        Assert.True(await new PostgresLegalHoldReader(db.DataSource).IsPersonHeldAsync(Guid.Parse(person), Ct));

        Assert.True(await store.ReleaseAsync(hold, "case closed", "owner-1", "device-1", at, Ct));

        Assert.DoesNotContain(await store.ListActiveAsync(Ct), h => h.HoldId == hold);
        Assert.False(await new PostgresLegalHoldReader(db.DataSource).IsPersonHeldAsync(Guid.Parse(person), Ct));
        Assert.Equal(["legal_hold.created", "legal_hold.released"], audit.Entries.Select(e => e.Kind));
        Assert.All(audit.Entries, e => Assert.Contains(hold.ToString("D"), e.Payload));
    }

    [Fact]
    public async Task Releasing_an_unknown_or_released_hold_appends_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var audit = new HoldAuditLog();
        var store = new PostgresLegalHoldStore(db.DataSource, audit);

        Assert.False(await store.ReleaseAsync(Guid.NewGuid(), "x", "owner-1", "device-1", DateTimeOffset.UtcNow, Ct));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Racing_releases_of_one_hold_append_one_event()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var audit = new HoldAuditLog();
        var store = new PostgresLegalHoldStore(db.DataSource, audit);
        var hold = Guid.NewGuid();
        await store.CreateAsync(hold, LegalHoldScopeKind.Gc, "gc-1", "case 7", "owner-1", "device-1", DateTimeOffset.UtcNow, Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            store.ReleaseAsync(hold, "done", "owner-1", "device-1", DateTimeOffset.UtcNow, Ct)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, audit.Entries.Count(e => e.Kind == "legal_hold.released"));
    }
}
