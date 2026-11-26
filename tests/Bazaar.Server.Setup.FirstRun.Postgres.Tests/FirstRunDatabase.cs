using System.Reflection;
using System.Text.RegularExpressions;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Setup.FirstRun.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>
/// A migrated database holding what the seed touches: the GC tables (common 0001), the gateway tables (0002), the agent tables
/// (0010), the <c>human</c> table (remote 0001) and this module's own files. The Vault index needs pgvector, so the test makes the
/// two columns the acceptance reads as a plain stub table instead.
/// </summary>
internal static partial class FirstRunDatabase
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return PgTestDatabase.CreateAsync("firstrun", MigrateAsync, ct);
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await new MigrationRunner(dataSource, new MigrationOptions(PartitionCount: 1)).MigrateAsync(new Sources(), MigrateCommand.CurrentProvider(), ct);
        await using var stub = dataSource.CreateCommand("""
            CREATE TABLE vault_docs (
                doc_id uuid PRIMARY KEY DEFAULT uuidv7(),
                type text NOT NULL,
                head_commit char(40),
                updated_at timestamptz NOT NULL DEFAULT now()
            )
            """);
        await stub.ExecuteNonQueryAsync(ct);

        // The Agents module's own migration (agents 0003) adds this column; the seed writes it, so the test database carries it too.
        await using var model = dataSource.CreateCommand("ALTER TABLE agent ADD COLUMN IF NOT EXISTS model text NULL");
        await model.ExecuteNonQueryAsync(ct);
    }

    private sealed class Sources : IMigrationSource
    {
        private static readonly EmbeddedMigrationSource _server = new(typeof(Bazaar.Server.AssemblyMarker).Assembly);

        public IReadOnlyList<string> Namespaces() => [MigrationNamespaces.Common, "remote", "setup_firstrun"];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
        {
            MigrationNamespaces.Common => [.. _server.Load(ns).Where(f => f.Version is 1 or 2 or 10)],
            "remote" => [.. _server.Load(ns).Where(f => f.Version is 1)],
            "setup_firstrun" => [.. OwnFiles()],
            _ => [],
        };

        private static IEnumerable<MigrationFile> OwnFiles()
        {
            var assembly = typeof(PostgresFirstRunStore).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var match = ResourcePattern().Match(resource);
                if (!match.Success || !MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name)) continue;
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                yield return new MigrationFile(match.Groups["namespace"].Value, version, name, reader.ReadToEnd());
            }
        }
    }

    [GeneratedRegex(@"\.Sql\.components\.(?<namespace>[^.]+)\.(?<file>[^.]+\.sql)$")]
    private static partial Regex ResourcePattern();

    // Test data helpers.
    public static async Task<Guid> AddOwnerAsync(NpgsqlDataSource dataSource, CancellationToken ct, string status = "active")
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES (@id, 'Owner', 'owner', @status)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public static async Task AddModelAsync(NpgsqlDataSource dataSource, string provider, string model, CancellationToken ct, bool providerEnabled = true, string family = "claude", string status = "available")
    {
        await using (var command = dataSource.CreateCommand("""
            INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind, enabled) VALUES (@id, 'first_party', 'anthropic_messages', 'https://example.invalid', 'none', @enabled)
            ON CONFLICT (id) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("id", provider);
            command.Parameters.AddWithValue("enabled", providerEnabled);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var insert = dataSource.CreateCommand("""
            INSERT INTO gw_model (provider_id, model_id, family, identity, status, first_seen, last_seen, source)
            VALUES (@p, @m, @f, 'native', @s, now(), now(), 'test')
            """);
        insert.Parameters.AddWithValue("p", provider);
        insert.Parameters.AddWithValue("m", model);
        insert.Parameters.AddWithValue("f", family);
        insert.Parameters.AddWithValue("s", status);
        await insert.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }
}
