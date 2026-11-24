using Bazaar.Contracts.GitHub;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Bazaar.GitHub.Wire;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.GitHub.Tests.Wire;

/// <summary>The PR read model behind <c>pr.list</c> and <c>pr.summary</c> (doc 21, Client PR wire) against a real <c>github_pr</c> table.</summary>
public sealed class PgGitHubPrReadModelTests
{
    private static readonly string _head = new('a', 40);

    private sealed class Adapter : IGitHubAdapter
    {
        public PrChecksState Checks { get; set; } = PrChecksState.Passing;
        public List<string> CheckedHeads { get; } = [];

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PrFile>>([new PrFile("a.cs", "modified", 3, 1, null), new PrFile("b.cs", "added", 5, 0, null)]);

        public Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
        {
            CheckedHeads.Add(headSha);
            return Task.FromResult(Checks);
        }

        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RepositoryEventsPage> ListRepositoryEventsAsync(RepositoryEventsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetRepositoryNodeIdAsync(long installationId, string owner, string repo, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AgentDirectory(string agentLogin) : IGitHubGcDirectory
    {
        public Task<Guid?> ResolveGcAsync(GcTarget target, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

        public Task<Guid?> ResolveAgentAsync(string login, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(login == agentLogin ? Guid.NewGuid() : null);
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        foreach (var suffix in new[] { "0001_github_tables.sql", "0004_github_pr_read_model.sql", "0008_github_pr_head_ref.sql", "0009_github_pr_repo_revision.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task AddAsync(NpgsqlDataSource ds, string node, string? repo, int number, string state, string author, DateTimeOffset updated, CancellationToken ct, Guid? card = null)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO github_pr (pr_node_id, repo_node_id, number, gc_id, task_id, head_sha, base_ref, state, draft, updated_at, title, author_login, repo_full_name)
            VALUES (@node, 'R_1', @number, gen_random_uuid(), @card, @sha, 'main', @state, false, @updated, @title, @author, @repo)
            """);
        cmd.Parameters.AddWithValue("node", node);
        cmd.Parameters.AddWithValue("number", number);
        cmd.Parameters.AddWithValue("card", (object?)card ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sha", _head);
        cmd.Parameters.AddWithValue("state", state);
        cmd.Parameters.AddWithValue("updated", updated);
        cmd.Parameters.AddWithValue("title", $"PR {number}");
        cmd.Parameters.AddWithValue("author", author);
        cmd.Parameters.AddWithValue("repo", (object?)repo ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PgGitHubPrReadModel Model(NpgsqlDataSource ds, Adapter adapter, long installation = 99) =>
        new(ds, adapter, new AgentDirectory("bot-bazaar[bot]"), installation);

    private static Task<PgTestDatabase> CreateDbAsync(string name, Func<NpgsqlDataSource, CancellationToken, Task> migrate, CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return PgTestDatabase.CreateAsync(name, migrate, ct);
    }

    [Fact]
    public async Task List_pages_newest_first_and_names_the_repo_installation_and_author_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadlist", MigrateAsync, ct);
        var now = DateTimeOffset.UtcNow;
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 1, "open", "bot-bazaar[bot]", now.AddMinutes(-3), ct);
        await AddAsync(db.DataSource, "PR_2", "acme/bazaar", 2, "open", "alice", now.AddMinutes(-2), ct);
        await AddAsync(db.DataSource, "PR_3", "acme/bazaar", 3, "closed", "alice", now.AddMinutes(-1), ct);
        var model = Model(db.DataSource, new Adapter());

        var first = await model.ListAsync(new PrListRequest(null, PrListState.Open, null, 1), ct);
        var second = await model.ListAsync(new PrListRequest(null, PrListState.Open, first.NextCursor, 1), ct);

        var one = Assert.Single(first.Rows);
        Assert.Equal("PR_2", one.PrId);
        Assert.Equal(PrAuthorKind.Human, one.AuthorKind);
        Assert.Equal(99, one.InstallationId);
        Assert.Equal("acme", one.Owner);
        Assert.Equal("bazaar", one.RepoName);
        Assert.Equal(PrChecksRollup.Passing, one.Checks);
        var two = Assert.Single(second.Rows);
        Assert.Equal("PR_1", two.PrId);
        Assert.Equal(PrAuthorKind.Agent, two.AuthorKind);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task List_filters_by_state_and_repo_and_skips_rows_the_PR_service_has_not_named()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadfilter", MigrateAsync, ct);
        var now = DateTimeOffset.UtcNow;
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 1, "open", "alice", now, ct);
        await AddAsync(db.DataSource, "PR_2", "acme/other", 2, "open", "alice", now, ct);
        await AddAsync(db.DataSource, "PR_3", null, 3, "open", "alice", now, ct);
        await AddAsync(db.DataSource, "PR_4", "acme/bazaar", 4, "merged", "alice", now, ct);
        var model = Model(db.DataSource, new Adapter());

        var repo = await model.ListAsync(new PrListRequest("ACME/bazaar", PrListState.Open, null, null), ct);
        var all = await model.ListAsync(new PrListRequest(null, PrListState.All, null, null), ct);
        var merged = await model.ListAsync(new PrListRequest(null, PrListState.Merged, null, null), ct);

        Assert.Equal(["PR_1"], repo.Rows.Select(r => r.PrId));
        Assert.Equal(3, all.Rows.Count);
        Assert.Equal(["PR_4"], merged.Rows.Select(r => r.PrId));
    }

    [Fact]
    public async Task A_garbage_cursor_is_the_first_page_and_the_limit_is_clamped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadcursor", MigrateAsync, ct);
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 1, "open", "alice", DateTimeOffset.UtcNow, ct);
        var model = Model(db.DataSource, new Adapter());

        var reply = await model.ListAsync(new PrListRequest(null, PrListState.Open, "not base64 !!", 100000), ct);

        Assert.Single(reply.Rows);
        Assert.False(PrListCursor.TryDecode("###", out _));
        Assert.False(PrListCursor.TryDecode(Convert.ToBase64String("-5|x"u8.ToArray()), out _));
    }

    [Fact]
    public async Task Resolve_and_summary_use_the_stored_head_and_card_and_unknown_PRs_are_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadsummary", MigrateAsync, ct);
        var card = Guid.NewGuid();
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 7, "open", "alice", DateTimeOffset.UtcNow, ct, card);
        var adapter = new Adapter { Checks = PrChecksState.Failing };
        var model = Model(db.DataSource, adapter);

        var pr = await model.ResolveAsync("PR_1", ct);
        var summary = await model.GetSummaryAsync("PR_1", pr!, ct);

        Assert.Equal(new PrRef(99, "acme", "bazaar", 7), pr);
        Assert.Null(await model.ResolveAsync("PR_missing", ct));
        Assert.Equal(_head, summary!.HeadSha);
        Assert.Equal(card, summary.LinkedCardId);
        Assert.Equal(PrChecksRollup.Failing, summary.Checks);
        Assert.False(summary.ReviewerApproved);
        Assert.Equal("2 files changed, +8 -1", summary.Summary);
        Assert.Equal(7, summary.Number);
        Assert.Equal([_head], adapter.CheckedHeads);
    }

    [Fact]
    public async Task Without_an_installation_or_a_repo_name_a_PR_cannot_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadnoinst", MigrateAsync, ct);
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 1, "open", "alice", DateTimeOffset.UtcNow, ct);
        await AddAsync(db.DataSource, "PR_2", null, 2, "open", "alice", DateTimeOffset.UtcNow, ct);

        Assert.Null(await Model(db.DataSource, new Adapter(), installation: 0).ResolveAsync("PR_1", ct));
        Assert.Null(await Model(db.DataSource, new Adapter()).ResolveAsync("PR_2", ct));
    }

    [Fact]
    public async Task The_revision_rises_only_when_a_row_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync("ghreadrev", MigrateAsync, ct);
        await AddAsync(db.DataSource, "PR_1", "acme/bazaar", 1, "open", "alice", DateTimeOffset.UtcNow, ct);

        async Task<long> RevisionAsync()
        {
            await using var read = db.DataSource.CreateCommand("SELECT revision FROM github_pr WHERE pr_node_id = 'PR_1'");
            return (long)(await read.ExecuteScalarAsync(ct))!;
        }

        await using (var same = db.DataSource.CreateCommand("UPDATE github_pr SET draft = false WHERE pr_node_id = 'PR_1'"))
        {
            await same.ExecuteNonQueryAsync(ct);
        }

        var unchanged = await RevisionAsync();
        await using (var change = db.DataSource.CreateCommand("UPDATE github_pr SET draft = true WHERE pr_node_id = 'PR_1'"))
        {
            await change.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(1, unchanged);
        Assert.Equal(2, await RevisionAsync());
    }

    [Fact]
    public async Task The_empty_read_model_knows_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var empty = new EmptyGitHubPrReadModel();

        Assert.Empty((await empty.ListAsync(new PrListRequest(null, PrListState.All, null, null), ct)).Rows);
        Assert.Null(await empty.ResolveAsync("x", ct));
    }
}
