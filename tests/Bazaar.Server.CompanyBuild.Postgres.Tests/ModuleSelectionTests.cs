using Bazaar.Core;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.WebAdmin;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CompanyBuild.Postgres.Tests;

/// <summary>The migration as embedded in the assembly (no database needed).</summary>
public class ModuleSelectionMigrationTests
{
    [Fact]
    public void Module_selection_namespace_holds_migration_0001()
    {
        var source = new EmbeddedMigrationSource(typeof(PostgresModuleSelection).Assembly);
        Assert.Contains("module_selection", source.Namespaces());
        var file = Assert.Single(source.Load("module_selection"), f => f.Version == 1);
        Assert.Equal("rebuild_queue", file.Name);
        Assert.Contains("CREATE TABLE module_rebuild (", file.Sql);
        Assert.Contains("module_rebuild_one_active", file.Sql);
        Assert.Empty(source.Load(MigrationRunner.Common));
    }

    [Fact]
    public void Selection_lives_under_the_documented_settings_key() =>
        Assert.Equal("modules.selection", PostgresModuleSelection.SelectionKey);
}

/// <summary>
/// The store against PostgreSQL. The server comes from <c>BAZAAR_TEST_PG</c>, or a Testcontainers container when it is unset
/// (<see cref="PgSource"/>); on Windows without <c>BAZAAR_TEST_PG</c> the tests skip. Each test gets its own database.
/// </summary>
public class PostgresModuleSelectionTests
{
    private static readonly Guid _actor = Guid.NewGuid();

    private static readonly ComponentManifest[] _manifests =
    [
        new("core", "1.0.0", ComponentKind.Kernel, [], []),
        new("kanban", "1.0.0", ComponentKind.Feature, ["core"], []),
        new("vault", "1.0.0", ComponentKind.Feature, ["core"], []),
    ];

    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync("modsel", MigrateAsync, ct);
        await PostgresModuleCatalogue.PublishAsync(db.DataSource, _manifests, TimeProvider.System, ct);
        return db;
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        // Only the settings table (common/0003) is taken from the server's frozen common namespace, so the test needs no pgvector; the module's own files come from its assembly.
        var common = new EmbeddedMigrationSource(typeof(Bazaar.Server.AssemblyMarker).Assembly).Load(MigrationRunner.Common).Where(f => f.Name == "settings");
        var own = new EmbeddedMigrationSource(typeof(PostgresModuleSelection).Assembly).Load("module_selection");
        await new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource([.. common, .. own]), MigrateCommand.CurrentProvider(), ct);
    }

    private static PostgresModuleSelection Store(PgTestDatabase db) => new(db.DataSource, TimeProvider.System);

    [Fact]
    public async Task Before_any_commit_the_revision_is_zero_and_every_module_is_selected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var catalogue = await Store(db).GetCatalogueAsync(ct);
        Assert.Equal(0, catalogue.Revision);
        Assert.Equal(3, catalogue.Modules.Count);
        Assert.All(catalogue.Modules, m => Assert.True(m.Selected));
    }

    [Fact]
    public async Task Preview_reports_missing_dependencies_unknown_ids_and_kernel_removal_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);

        var preview = await store.PreviewAsync(["kanban", "ghost"], ct);

        Assert.False(preview.Valid);
        Assert.Contains(preview.Problems, p => p.ModuleId == "ghost");
        Assert.Contains(preview.Problems, p => p.ModuleId == "core" && p.Message.Contains("kernel"));
        Assert.Contains(preview.Problems, p => p.ModuleId == "kanban");
        Assert.Equal(0, (await store.GetCatalogueAsync(ct)).Revision);
    }

    [Fact]
    public async Task Valid_preview_lists_what_is_added_and_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);

        var preview = await Store(db).PreviewAsync(["core", "kanban"], ct);

        Assert.True(preview.Valid);
        Assert.Equal(["vault"], preview.Removed);
        Assert.Empty(preview.Added);
    }

    [Fact]
    public async Task Commit_against_the_current_revision_bumps_it_and_a_stale_revision_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);

        var first = await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);
        Assert.True(first.Committed);
        Assert.Equal(1, first.Revision);

        var stale = await store.CommitAsync(_actor, ["core", "vault"], 0, ct);
        Assert.False(stale.Committed);
        Assert.Equal(1, stale.Revision);

        var second = await store.CommitAsync(_actor, ["core", "vault"], 1, ct);
        Assert.True(second.Committed);
        Assert.Equal(2, second.Revision);

        var catalogue = await store.GetCatalogueAsync(ct);
        Assert.Equal(2, catalogue.Revision);
        Assert.Equal(["core", "vault"], catalogue.Modules.Where(m => m.Selected).Select(m => m.Id).Order().ToArray());
    }

    [Fact]
    public async Task Selection_is_stored_in_the_settings_table_under_modules_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await Store(db).CommitAsync(_actor, ["core"], 0, ct);

        await using var cmd = db.DataSource.CreateCommand("SELECT value->>'revision' FROM setting WHERE key = 'modules.selection' AND scope = 'install'");
        Assert.Equal("1", await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Two_commits_racing_on_the_same_revision_have_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban", "vault"], 0, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CommitAsync(_actor, ["core", "kanban"], 1, ct)));

        Assert.Equal(1, results.Count(r => r.Committed));
        Assert.Equal(2, (await store.GetCatalogueAsync(ct)).Revision);
    }

    [Fact]
    public async Task An_invalid_commit_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);

        var result = await store.CommitAsync(_actor, ["kanban"], 0, ct);

        Assert.False(result.Committed);
        Assert.NotEmpty(result.Problems);
        Assert.Equal(0, (await store.GetCatalogueAsync(ct)).Revision);
    }

    [Fact]
    public async Task Rebuild_needs_a_committed_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);

        var start = await Store(db).StartRebuildAsync(_actor, "k1", ct);

        Assert.False(start.Started);
        Assert.Null(start.BuildId);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_first_job_and_a_new_key_for_the_same_revision_returns_it_too()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);

        var first = await store.StartRebuildAsync(_actor, "k1", ct);
        var again = await store.StartRebuildAsync(_actor, "k1", ct);
        var otherKey = await store.StartRebuildAsync(_actor, "k2", ct);

        Assert.True(first.Started);
        Assert.NotNull(first.BuildId);
        Assert.Equal(first, again);
        Assert.True(otherKey.Started);
        Assert.Equal(first.BuildId, otherKey.BuildId);
        Assert.Single(await store.GetBuildsAsync(ct));
    }

    [Fact]
    public async Task A_second_rebuild_for_a_newer_revision_is_refused_while_one_is_active()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);
        var first = await store.StartRebuildAsync(_actor, "k1", ct);
        await store.CommitAsync(_actor, ["core", "vault"], 1, ct);

        var second = await store.StartRebuildAsync(_actor, "k2", ct);

        Assert.False(second.Started);
        Assert.Equal(first.BuildId, second.BuildId);
        Assert.Contains("already running", second.Reason);
    }

    [Fact]
    public async Task Concurrent_starts_create_one_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.StartRebuildAsync(_actor, $"k{i}", ct)));

        Assert.Single(results.Select(r => r.BuildId).Distinct());
        Assert.Single(await store.GetBuildsAsync(ct));
    }

    [Fact]
    public async Task Job_runs_queued_running_succeeded_and_then_a_newer_revision_can_build()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);
        var start = await store.StartRebuildAsync(_actor, "k1", ct);
        Assert.Equal("queued", Assert.Single(await store.GetBuildsAsync(ct)).State);

        var job = await store.ClaimNextAsync(ct);
        Assert.NotNull(job);
        Assert.Equal(start.BuildId, job.BuildId);
        Assert.Equal(1, job.SelectionRevision);
        Assert.Equal(["core", "kanban"], job.SelectedModuleIds.Order().ToArray());
        Assert.Equal("running", Assert.Single(await store.GetBuildsAsync(ct)).State);
        Assert.Null(await store.ClaimNextAsync(ct));

        Assert.True(await store.CompleteAsync(job.BuildId, true, "ok", ct));
        Assert.False(await store.CompleteAsync(job.BuildId, true, "again", ct));
        var record = Assert.Single(await store.GetBuildsAsync(ct));
        Assert.Equal("succeeded", record.State);
        Assert.Equal("ok", record.ReportSummary);
        Assert.NotNull(record.FinishedAt);
        Assert.Equal(1, record.Attempt);

        await store.CommitAsync(_actor, ["core", "vault"], 1, ct);
        Assert.True((await store.StartRebuildAsync(_actor, "k2", ct)).Started);
    }

    [Fact]
    public async Task A_failed_rebuild_is_not_retried_for_the_same_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = Store(db);
        await store.CommitAsync(_actor, ["core", "kanban"], 0, ct);
        await store.StartRebuildAsync(_actor, "k1", ct);
        var job = await store.ClaimNextAsync(ct);
        await store.CompleteAsync(job!.BuildId, false, "boom", ct);

        var retry = await store.StartRebuildAsync(_actor, "k2", ct);

        Assert.False(retry.Started);
        Assert.Equal(job.BuildId, retry.BuildId);
    }

    [Fact]
    public async Task Catalogue_publish_replaces_the_snapshot_and_prunes_removed_modules()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);

        await PostgresModuleCatalogue.PublishAsync(db.DataSource, _manifests.Where(m => m.Id != "vault"), TimeProvider.System, ct);

        var catalogue = await Store(db).GetCatalogueAsync(ct);
        Assert.Equal(["core", "kanban"], catalogue.Modules.Select(m => m.Id).ToArray());
        Assert.Equal(["core"], catalogue.Modules.Single(m => m.Id == "kanban").DependsOn);
        Assert.Equal("kernel", catalogue.Modules.Single(m => m.Id == "core").Tag);
    }
}
