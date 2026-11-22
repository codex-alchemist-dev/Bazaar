using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Providers.Tests;

/// <summary>
/// A per-class database with only the search table migration (0007) applied. Migration 0001 creates cluster-wide roles and
/// cannot run in every per-class database; 0007 has no foreign keys. The DDL is Bazaar.Server's common migration (frozen
/// namespace), linked into this project as an embedded resource so the tests need no Bazaar.Server reference.
/// </summary>
public sealed class SearchDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await using var stream = typeof(SearchDb).Assembly.GetManifestResourceStream("0007_search_document.sql")
            ?? throw new InvalidOperationException("The search table migration is not embedded in this test assembly.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
