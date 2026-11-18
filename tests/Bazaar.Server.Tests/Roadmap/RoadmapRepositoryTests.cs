using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.Tests.Constellation;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>
/// The roadmap read path on real PostgreSQL: documents from <c>vault_docs</c>, progress from <c>task_links</c> and <c>tasks</c>
/// (migration <c>roadmap/0001</c>, doc 38 Layer 3).
/// </summary>
public sealed class RoadmapRepositoryTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task InsertDocAsync(NpgsqlDataSource ds, Guid vaultId, string path, string frontMatterJson)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO vault_docs (vault_id, path, title, type, area, frontmatter)
            VALUES ($1, $2, $3, 'plan', 'roadmap', $4::jsonb)
            """);
        cmd.Parameters.AddWithValue(vaultId);
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.AddWithValue(frontMatterJson);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task InsertLinkedTaskAsync(NpgsqlDataSource ds, Guid gcId, string objectivePath, string state)
    {
        var taskId = Guid.CreateVersion7();
        await using (var task = ds.CreateCommand("""
            INSERT INTO tasks (id, project_id, title, state, origin_gc_id)
            VALUES ($1, $2, $3, $4, $5)
            """))
        {
            task.Parameters.AddWithValue(taskId);
            task.Parameters.AddWithValue(Guid.CreateVersion7());
            task.Parameters.AddWithValue("Task for " + objectivePath);
            task.Parameters.AddWithValue(state);
            task.Parameters.AddWithValue(gcId);
            await task.ExecuteNonQueryAsync(Ct);
        }

        await using var link = ds.CreateCommand("INSERT INTO task_links (task_id, kind, ref) VALUES ($1, 'vault_doc', $2)");
        link.Parameters.AddWithValue(taskId);
        link.Parameters.AddWithValue(objectivePath);
        await link.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Loads_the_roadmap_of_one_vault_with_task_progress_and_reports_a_bad_document()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var seed = await ConstellationSeed.InsertAsync(ds);
        var vaultId = seed.VaultId;
        var tag = seed.Tag;
        var phase = $"roadmap/phases/{tag}.md";
        var objective = $"roadmap/objectives/a-{tag}.md";

        await InsertDocAsync(ds, vaultId, phase, """{"kind":"phase","status":"active"}""");
        await InsertDocAsync(ds, vaultId, objective, $$"""{"kind":"objective","phase":"{{tag}}","status":"active"}""");
        await InsertDocAsync(ds, vaultId, $"roadmap/objectives/bad-{tag}.md", """{"kind":"objective","phase":"x","status":"maybe"}""");
        await InsertDocAsync(ds, Guid.CreateVersion7(), $"roadmap/objectives/other-{tag}.md", """{"kind":"objective","phase":"x","status":"done"}""");
        await InsertLinkedTaskAsync(ds, seed.GeneralGc, objective, "done");
        await InsertLinkedTaskAsync(ds, seed.GeneralGc, objective, "in_progress");

        var load = await RoadmapRepository.LoadAsync(ds, vaultId, Ct);

        var objectiveDoc = Assert.Single(load.Documents, d => d.Path == objective);
        Assert.IsType<RoadmapObjective>(objectiveDoc.Document);
        Assert.Contains(load.Documents, d => d.Path == phase && d.Slug == tag);
        Assert.DoesNotContain(load.Documents, d => d.Path.Contains("other-" + tag, StringComparison.Ordinal));
        Assert.Equal($"roadmap/objectives/bad-{tag}.md", Assert.Single(load.Errors).Path);

        var states = load.StatesByPath[objective];
        Assert.Equal(2, states.Count);
        Assert.Contains("done", states);
        Assert.Contains("in_progress", states);

        var view = RoadmapReadModel.Build(load.Documents, load.StatesByPath);
        var progress = Assert.Single(view.Phases[0].Objectives).Progress;
        Assert.Equal(2, progress.LinkedTasks);
        Assert.Equal(0.5m, progress.DoneShare);
    }

    [Fact]
    public async Task A_vault_with_no_roadmap_documents_loads_empty()
    {
        SkipWithoutDatabase();

        var load = await RoadmapRepository.LoadAsync(db.DataSource, Guid.CreateVersion7(), Ct);

        Assert.Empty(load.Documents);
        Assert.Empty(load.Errors);
        Assert.Empty(load.StatesByPath);
    }
}
