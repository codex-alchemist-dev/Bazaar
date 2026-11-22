using Bazaar.Server.PsychWard;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.PsychWard.Tests;

/// <summary>
/// A throwaway database with only the <c>psych_ward</c> component migrations applied. The tables have no foreign keys into
/// common, so the embedded SQL is applied directly in file-name order and the full server schema is not needed.
/// </summary>
public sealed class PsychWardDb : IAsyncLifetime
{
    private PgSource? _source;
    private string _database = "";

    /// <summary>Pooled data source for this class's database.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _source = await PgSource.GetSharedAsync();
        _database = "psych_ward_" + Guid.NewGuid().ToString("N");
        await using (var admin = NpgsqlDataSource.Create(_source.AdminConnectionString))
        {
            await using var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\"");
            await create.ExecuteNonQueryAsync();
        }

        DataSource = NpgsqlDataSource.Create(_source.ConnectionStringFor(_database));

        var assembly = typeof(PgWardStayStore).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.psych_ward.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(names);

        // The module reads agent names from the runtime's agent table (common/0010). A minimal stand-in keeps this test off the full schema.
        await using (var humanTable = DataSource.CreateCommand(
            "CREATE TABLE human (id uuid PRIMARY KEY, display_name text NOT NULL, role text NOT NULL, status text NOT NULL)"))
        {
            await humanTable.ExecuteNonQueryAsync();
        }

        await using (var agentTable = DataSource.CreateCommand(
            "CREATE TABLE agent (agent_id uuid PRIMARY KEY, name text NOT NULL CHECK (length(name) BETWEEN 1 AND 64))"))
        {
            await agentTable.ExecuteNonQueryAsync();
        }

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(_source!.AdminConnectionString);
        await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}
