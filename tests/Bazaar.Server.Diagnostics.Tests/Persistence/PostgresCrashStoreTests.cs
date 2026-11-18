using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Diagnostics.Tests.Persistence;

public sealed class PostgresCrashStoreTests : CrashStoreContract
{
    protected override async Task<(ICrashIntakeStore Store, ICrashActionOutbox Outbox, IAsyncDisposable Handle)> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync(
            "crash",
            async (ds, token) =>
            {
                await PgRoles.EnsureNoLoginRoleAsync(ds, "bazaar_app", token).ConfigureAwait(false);
                await new MigrationRunner(ds).MigrateAsync(
                    new EmbeddedMigrationSource(typeof(PostgresCrashStore).Assembly), MigrateCommand.CurrentProvider(), token).ConfigureAwait(false);
            },
            ct);
        var store = new PostgresCrashStore(db.DataSource);
        return (store, store, db);
    }
}
