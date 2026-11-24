using Bazaar.Server.GeneticMemory;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.GeneticMemory;

/// <summary>
/// A cloned test database with the <c>genetic_memory</c> component migrations applied. The table have no foreign keys into
/// common, so the embedded component SQL is applied directly, in file-name order (the same approach the Vms tests use).
/// </summary>
public sealed class GmtDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 8 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("GmtDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database; each test skips itself.
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);

        var assembly = typeof(PgGmtLedger).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.genetic_memory.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No genetic-memory migrations are embedded in Bazaar.Server.GeneticMemory.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            await using var cmd = DataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Removes every candidate row so each test starts empty.</summary>
    public async Task ClearAsync()
    {
        await using var cmd = DataSource.CreateCommand("TRUNCATE gmt_candidate");
        await cmd.ExecuteNonQueryAsync();
    }
}
