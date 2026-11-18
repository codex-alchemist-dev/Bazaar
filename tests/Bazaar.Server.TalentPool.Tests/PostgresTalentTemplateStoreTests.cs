using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.TalentPool.Tests;

/// <summary>The store contract against real PostgreSQL, plus what only the database can enforce.</summary>
public sealed class PostgresTalentTemplateStoreTests : TalentTemplateStoreContract
{
    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresTalentTemplateStore).Assembly), MigrateCommand.CurrentProvider(), ct);

    private static async Task<PgTestDatabase> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("talent", MigrateAsync, ct);
    }

    internal override async Task<(ITalentTemplateStore Store, IAsyncDisposable Lease)> OpenAsync(CancellationToken ct)
    {
        var db = await CreateAsync(ct);
        return (new PostgresTalentTemplateStore(db.DataSource), db);
    }

    [Fact]
    public async Task Concurrent_submitters_get_distinct_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);
        var store = new PostgresTalentTemplateStore(db.DataSource);
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.AddVersionAsync(id, Samples.Validated(Samples.Manifest(prompt: $"Prompt {i}.")), null, "agent:alice", ct)));

        Assert.Equal(Enumerable.Range(1, 8), results.Select(r => r.Template.Version).Order());
    }

    [Fact]
    public async Task A_store_over_a_fresh_data_source_sees_earlier_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);
        var id = Guid.NewGuid();
        await new PostgresTalentTemplateStore(db.DataSource).AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);

        await using var second = NpgsqlDataSource.Create(db.ConnectionString);
        var read = await new PostgresTalentTemplateStore(second).GetAsync(id, 1, ct);

        Assert.NotNull(read);
    }

    [Fact]
    public async Task A_manifest_stored_under_the_wrong_hash_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);
        var forged = Samples.Validated() with { ContentSha = new string('0', 64) };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PostgresTalentTemplateStore(db.DataSource).AddVersionAsync(Guid.NewGuid(), forged, null, "agent:alice", ct));
    }

    [Theory]
    [InlineData("DELETE FROM talent_templates")]
    [InlineData("UPDATE talent_templates SET content_sha = repeat('1', 64)")]
    [InlineData("UPDATE talent_templates SET manifest = '{\"prompt\":\"x\",\"tools\":[],\"models\":[]}'")]
    [InlineData("TRUNCATE talent_templates")]
    public async Task The_database_refuses_edits_to_stored_content(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);
        await new PostgresTalentTemplateStore(db.DataSource).AddVersionAsync(Guid.NewGuid(), Samples.Validated(), null, "agent:alice", ct);

        await using var cmd = db.DataSource.CreateCommand(sql);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
    }

    [Fact]
    public async Task The_database_refuses_a_manifest_that_carries_memory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);

        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO talent_templates (template_id, version, manifest, content_sha, submitted_by) " +
            "VALUES (gen_random_uuid(), 1, '{\"prompt\":\"p\",\"tools\":[],\"models\":[],\"memory\":[\"x\"]}', repeat('a', 64), 'test')");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task The_database_refuses_a_second_status_change_even_around_the_store()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateAsync(ct);
        var store = new PostgresTalentTemplateStore(db.DataSource);
        var id = Guid.NewGuid();
        await store.AddVersionAsync(id, Samples.Validated(), null, "agent:alice", ct);
        await store.TryResolveAsync(id, 1, TalentTemplateStatus.Rejected, ct);

        await using var cmd = db.DataSource.CreateCommand("UPDATE talent_templates SET status = 'hired'");
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
    }
}
