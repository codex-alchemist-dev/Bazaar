using System.Security.Cryptography;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Identity.Postgres.Tests;

public sealed class PostgresCredentialLeaseStoreTests
{
    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresTokenBudgetStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    /// <summary>A credential row, which every lease points at. Its data key row comes first, as the schema requires.</summary>
    private static async Task<CredentialId> InsertCredentialAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var dek = Guid.NewGuid();
        await using (var dekCmd = dataSource.CreateCommand(
            "INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id) VALUES ($1, 'test', $2, 'kek-1')"))
        {
            dekCmd.Parameters.AddWithValue(dek);
            dekCmd.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
            await dekCmd.ExecuteNonQueryAsync(ct);
        }

        var credential = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO credential (credential_id, soul_id, service, kind, handle, ciphertext, nonce, dek_id, tier, status) " +
            "VALUES ($1, NULL, 'github', 'app_key', 'handle', $2, $3, $4, 'ordinary', 'active')");
        cmd.Parameters.AddWithValue(credential);
        cmd.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(16));
        cmd.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(12));
        cmd.Parameters.AddWithValue(dek);
        await cmd.ExecuteNonQueryAsync(ct);
        return new CredentialId(credential);
    }

    [Fact]
    public async Task An_approval_backs_one_lease_even_across_a_restart()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("lease", MigrateAsync, ct);
        var credential = await InsertCredentialAsync(db.DataSource, ct);
        var agent = Guid.NewGuid().ToString();
        var approval = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        var first = await new PostgresCredentialLeaseStore(db.DataSource).IssueAsync(
            new LeaseId(Guid.NewGuid()), agent, credential, "read", expires, approval, ct);
        var replay = await new PostgresCredentialLeaseStore(db.DataSource).IssueAsync(
            new LeaseId(Guid.NewGuid()), agent, credential, "read", expires, approval, ct);
        var usedAfterRestart = await new PostgresCredentialLeaseStore(db.DataSource).IsApprovalUsedAsync(approval, ct);

        Assert.NotNull(first);
        Assert.Null(replay);
        Assert.True(usedAfterRestart);
    }

    [Fact]
    public async Task Revoking_an_agent_hides_its_active_leases_and_leaves_others_alone()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("lease", MigrateAsync, ct);
        var credential = await InsertCredentialAsync(db.DataSource, ct);
        var store = new PostgresCredentialLeaseStore(db.DataSource);
        var revoked = Guid.NewGuid().ToString();
        var kept = Guid.NewGuid().ToString();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var revokedLease = await store.IssueAsync(new LeaseId(Guid.NewGuid()), revoked, credential, "read", expires, null, ct);
        var keptLease = await store.IssueAsync(new LeaseId(Guid.NewGuid()), kept, credential, "read", expires, null, ct);

        await store.RevokeAgentAsync(revoked, ct);

        Assert.Empty(await store.ListActiveAsync(revoked, ct));
        Assert.Single(await store.ListActiveAsync(kept, ct));
        Assert.Null(await store.GetAsync(revokedLease!.Id, revoked, ct));
        Assert.NotNull(await store.GetAsync(keptLease!.Id, kept, ct));
    }

    [Fact]
    public async Task A_non_guid_agent_id_is_refused_by_the_durable_store()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("lease", MigrateAsync, ct);
        var store = new PostgresCredentialLeaseStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ListActiveAsync("agent-a", ct));
    }
}
