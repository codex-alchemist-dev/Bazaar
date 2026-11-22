using Bazaar.GitHub.Events;
using Bazaar.GitHub.Pr;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.GitHub.Tests.Pr;

/// <summary>The PR lookup for check and push events (doc 21, Event mapping; doc 40 GitHub) against a real <c>github_pr</c> table.</summary>
public sealed class GitHubPrLookupTests
{
    private static readonly string _shaA = new('a', 40);
    private static readonly string _shaB = new('b', 40);

    private sealed class FixedLookup(GitHubPrMatch? match) : IGitHubPrLookup
    {
        public int Calls { get; private set; }

        public Task<GitHubPrMatch?> FindOpenAsync(string repoNodeId, IReadOnlyList<int> numbers, string? headSha, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(match);
        }
    }

    private static GitHubEvent Check(string name = "check_run", string? pr = null, string? sha = null, params int[] numbers) => new()
    {
        Source = GitHubEventSource.Webhook,
        EventName = name,
        EventId = "d1",
        RepoNodeId = "R_1",
        PrNodeId = pr,
        HeadSha = sha,
        PullRequestNumbers = numbers,
        AuthorLogin = "github-actions[bot]",
        AuthorAssociation = "NONE",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        foreach (var suffix in new[] { "0001_github_tables.sql", "0004_github_pr_read_model.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task AddPrAsync(NpgsqlDataSource ds, string node, string repo, int number, string sha, string state, string? author, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO github_pr (pr_node_id, repo_node_id, number, gc_id, head_sha, base_ref, state, draft, updated_at, author_login)
            VALUES (@node, @repo, @number, gen_random_uuid(), @sha, 'main', @state, false, now(), @author)
            """);
        cmd.Parameters.AddWithValue("node", node);
        cmd.Parameters.AddWithValue("repo", repo);
        cmd.Parameters.AddWithValue("number", number);
        cmd.Parameters.AddWithValue("sha", sha);
        cmd.Parameters.AddWithValue("state", state);
        cmd.Parameters.AddWithValue("author", (object?)author ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Resolve_fills_the_PR_node_id_and_author_for_check_and_push_events_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var lookup = new FixedLookup(new GitHubPrMatch("PR_1", 7, "bob-bazaar[bot]"));

        var check = await GitHubPrResolver.ResolveAsync(lookup, Check(sha: _shaA), ct);
        var push = await GitHubPrResolver.ResolveAsync(lookup, Check("push", sha: _shaA), ct);
        var other = await GitHubPrResolver.ResolveAsync(lookup, Check("issues"), ct);
        var named = await GitHubPrResolver.ResolveAsync(lookup, Check(pr: "PR_9"), ct);

        Assert.Equal("PR_1", check.PrNodeId);
        Assert.Equal("bob-bazaar[bot]", check.PrAuthorLogin);
        Assert.Equal("PR_1", push.PrNodeId);
        Assert.Null(other.PrNodeId);
        Assert.Equal("PR_9", named.PrNodeId);
        Assert.Equal(2, lookup.Calls);
        Assert.Null((await GitHubPrResolver.ResolveAsync(null, Check(sha: _shaA), ct)).PrNodeId);
    }

    [Fact]
    public async Task Lookup_matches_by_number_first_then_by_head_sha_and_only_open_PRs_in_the_same_repo()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghlookup", MigrateAsync, ct);
        await AddPrAsync(db.DataSource, "PR_7", "R_1", 7, _shaA, "open", "bob-bazaar[bot]", ct);
        await AddPrAsync(db.DataSource, "PR_8", "R_1", 8, _shaB, "closed", "bob-bazaar[bot]", ct);
        await AddPrAsync(db.DataSource, "PR_X", "R_2", 7, _shaB, "open", null, ct);
        var lookup = new PgGitHubPrLookup(db.DataSource);

        Assert.Equal("PR_7", (await lookup.FindOpenAsync("R_1", [7], null, ct))!.PrNodeId);
        Assert.Equal("bob-bazaar[bot]", (await lookup.FindOpenAsync("R_1", [], _shaA, ct))!.AuthorLogin);
        Assert.Equal(7, (await lookup.FindOpenAsync("R_1", [99], _shaA, ct))!.Number);
        Assert.Null(await lookup.FindOpenAsync("R_1", [8], _shaB, ct));
        Assert.Equal("PR_X", (await lookup.FindOpenAsync("R_2", [], _shaB, ct))!.PrNodeId);
        Assert.Null(await lookup.FindOpenAsync("R_1", [], new string('c', 40), ct));
        Assert.Null(await lookup.FindOpenAsync("R_1", [], null, ct));
    }
}
