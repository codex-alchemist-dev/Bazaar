using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.ApprovalQueue.Tests;

/// <summary>Database-backed tests. They run only when <c>BAZAAR_TEST_PG</c> names a server; without it each one skips.</summary>
public sealed class PostgresApprovalStoreTests : PgFixture, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource? _pool;

    private static bool HasDatabase => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public override async ValueTask InitializeAsync()
    {
        if (!HasDatabase)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        // The server's core namespace (it creates the bazaar_app role) plus this module's approval_queue namespace.
        var sources = new[] { new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), new EmbeddedMigrationSource(typeof(ApprovalQueueComponent).Assembly) };
        var source = new InMemoryMigrationSource(sources.SelectMany(s => s.Namespaces().SelectMany(s.Load)));
        await new MigrationRunner(_pool, new MigrationOptions(16)).MigrateAsync(source, "linux", CancellationToken.None);
    }

    private PostgresApprovalStore Store()
    {
        Assert.SkipUnless(HasDatabase, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        return new PostgresApprovalStore(_pool!);
    }

    private static ApprovalItem NewItem(TimeSpan ttl, string kind = "mail.send") =>
        new(Guid.NewGuid(), kind, 1, "subject", Guid.NewGuid(), "ref:1", ApprovalStatus.Pending, _now, _now + ttl, null, null, null);

    [Fact]
    public async Task Insert_and_get_round_trip()
    {
        var store = Store();
        var item = NewItem(TimeSpan.FromHours(1));
        await store.InsertAsync(item);

        Assert.Equal(item, await store.GetAsync(item.Id));
        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Decide_works_once_and_not_after_expiry()
    {
        var store = Store();
        var live = NewItem(TimeSpan.FromHours(1));
        var stale = NewItem(TimeSpan.FromMinutes(1));
        await store.InsertAsync(live);
        await store.InsertAsync(stale);
        var approver = Guid.NewGuid();

        Assert.True(await store.TryDecideAsync(live.Id, approver, true, "ok", _now, CancellationToken.None));
        Assert.False(await store.TryDecideAsync(live.Id, approver, false, null, _now, CancellationToken.None));
        Assert.False(await store.TryDecideAsync(stale.Id, approver, true, null, _now.AddMinutes(2), CancellationToken.None));

        var read = await store.GetAsync(live.Id);
        Assert.Equal(ApprovalStatus.Approved, read!.Status);
        Assert.Equal(approver, read.DecidedBy);
        Assert.Equal("ok", read.Reason);
    }

    [Fact]
    public async Task Consume_is_true_exactly_once_under_concurrency()
    {
        var store = Store();
        var item = NewItem(TimeSpan.FromHours(1));
        await store.InsertAsync(item);
        await store.TryDecideAsync(item.Id, Guid.NewGuid(), true, null, _now, CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.TryConsumeAsync(item.Id, _now, CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(ApprovalStatus.Consumed, (await store.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task List_pending_skips_expired_decided_and_other_kinds()
    {
        var store = Store();
        var kind = "test." + Guid.NewGuid().ToString("N");
        var keep = NewItem(TimeSpan.FromHours(1), kind);
        var expired = NewItem(TimeSpan.FromMinutes(1), kind);
        var decided = NewItem(TimeSpan.FromHours(1), kind);
        var other = NewItem(TimeSpan.FromHours(1), kind + ".other");
        foreach (var i in new[] { keep, expired, decided, other })
            await store.InsertAsync(i);
        await store.TryDecideAsync(decided.Id, Guid.NewGuid(), false, null, _now, CancellationToken.None);

        var rows = await store.ListPendingAsync(kind, _now.AddMinutes(2));

        Assert.Equal([keep.Id], rows.Select(r => r.Id));
    }
}
