using Bazaar.Server.Vms.Environments;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Vms.Tests.Environments;

/// <summary>Migration components/vms/0001 against a real PostgreSQL: the five component tables exist and their constraints hold.</summary>
public sealed class EnvironmentMigrationTests(VmDb db) : IClassFixture<VmDb>
{
    private static readonly string[] _componentTables =
        ["environment_checkpoint", "environment_vm", "exec_log", "golden_image", "scoped_mount"];

    [Fact]
    public async Task Migration_creates_all_component_tables()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name IN ('golden_image', 'environment_vm', 'environment_checkpoint', 'scoped_mount', 'exec_log')
            ORDER BY table_name
            """);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            names.Add(reader.GetString(0));

        Assert.Equal(_componentTables, names);
    }

    [Fact]
    public async Task Golden_image_refuses_an_unknown_os()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO golden_image (id, os, version, sha256, guest_agent_version, state)
            VALUES (gen_random_uuid(), 'macos', 1, repeat('a', 64), '1.0.0', 'Ready')
            """);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Golden_image_refuses_a_second_version_for_the_same_os()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.InsertImageAsync(VmOs.Windows, version: 7);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InsertImageAsync(VmOs.Windows, version: 7));
        Assert.Equal("23505", ex.SqlState);
    }
}
