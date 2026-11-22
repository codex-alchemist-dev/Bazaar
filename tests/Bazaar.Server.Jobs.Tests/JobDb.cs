using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Jobs.Tests;

/// <summary>
/// A per-class database with only the job table (migration 0006) applied. The DDL is Bazaar.Server's common migration, linked
/// into this project as an embedded resource, so the tests need no Bazaar.Server reference. Migration 0001 creates cluster-wide
/// roles, so it cannot run again in every per-class database; 0006 has no foreign keys and needs nothing else.
/// </summary>
public sealed class JobDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await using var stream = typeof(JobDb).Assembly.GetManifestResourceStream("0006_job.sql")
            ?? throw new InvalidOperationException("The job table migration is not embedded in this test assembly.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
