using Bazaar.Server.CompanyMemory;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CompanyMemory.Tests;

/// <summary>A cloned test database with the <c>company_memory</c> component migrations applied.</summary>
public sealed class CompanyMemoryDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("CompanyMemoryDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);

        var assembly = typeof(PgDecisionLinkIndex).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.company_memory.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No company_memory migrations are embedded in Bazaar.Server.CompanyMemory.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task ClearAsync()
    {
        await using var cmd = DataSource.CreateCommand("TRUNCATE decision_link");
        await cmd.ExecuteNonQueryAsync();
    }
}
