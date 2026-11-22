using Bazaar.Contracts.Gc;
using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Bazaar.GitHub.Pr;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Bazaar.GitHub.Tests.Pr;

/// <summary>PR GC creation and the PR read model (doc 21 WP6), on a real Postgres with the github tables and a fake GC factory.</summary>
public sealed class GitHubPrServiceTests
{
    private const string _node = "PR_kwDOExample1";
    private const string _agentLogin = "charles-bazaar[bot]";
    private const string _reviewerLogin = "ada-bazaar[bot]";
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid _bridge = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid _charles = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid _ada = Guid.Parse("00000000-0000-0000-0000-00000000a002");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly string _sha1 = new('a', 40);
    private static readonly string _sha2 = new('b', 40);

    private sealed class FakeFactory : IGitHubPrGcFactory
    {
        private readonly Lock _gate = new();
        public Dictionary<Guid, List<Guid>> Members { get; } = [];
        public Dictionary<Guid, string> Titles { get; } = [];
        public int Creates { get; private set; }

        public async Task<bool> EnsureAsync(Guid gcId, string title, Guid createdBy, IReadOnlyList<Guid> members, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
        {
            await Task.Yield();
            lock (_gate)
            {
                if (Members.ContainsKey(gcId))
                    return false;
                Members[gcId] = [.. members];
                Titles[gcId] = title;
                Creates++;
                return true;
            }
        }

        public Task<bool> AddAgentAsync(Guid gcId, Guid agentId, string displayName, CancellationToken ct)
        {
            lock (_gate)
            {
                if (Members[gcId].Contains(agentId))
                    return Task.FromResult(false);
                Members[gcId].Add(agentId);
                return Task.FromResult(true);
            }
        }
    }

    private sealed class FakeDirectory : IGitHubGcDirectory
    {
        public Task<Guid?> ResolveGcAsync(GcTarget target, CancellationToken ct) => Task.FromResult<Guid?>(null);

        public Task<Guid?> ResolveAgentAsync(string login, CancellationToken ct) => Task.FromResult<Guid?>(login switch
        {
            _agentLogin => _charles,
            _reviewerLogin => _ada,
            _ => null,
        });
    }

    // The github tables only: 0005 registers a GC event kind and needs the core schema (the end-to-end test runs it).
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && !n.EndsWith("0005_github_gc_event_kind.sql", StringComparison.Ordinal) && !n.EndsWith("0007_github_issue_gc_type.sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class FakeOwner(Guid? id) : IGitHubOwnerResolver
    {
        public Guid? Id { get; set; } = id;

        public Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken ct) => Task.FromResult(Id);
    }

    private static GitHubPrService ServiceWithOwner(NpgsqlDataSource ds, FakeFactory factory, FakeOwner owner, GitHubPrGcOptions? options = null) =>
        new(ds, factory, new FakeDirectory(), options ?? new GitHubPrGcOptions(), _bridge, _bridge, NullLogger<GitHubPrService>.Instance, owner);

    private static GitHubPrService Service(NpgsqlDataSource ds, FakeFactory factory) =>
        new(ds, factory, new FakeDirectory(), new GitHubPrGcOptions(), _owner, _bridge, NullLogger<GitHubPrService>.Instance);

    private static GitHubEvent Opened(DateTimeOffset at, string? sha = null, string title = "Add PR service", string action = "opened", string? reviewer = null) => new()
    {
        Source = GitHubEventSource.Poll,
        EventName = "pull_request",
        Action = action,
        EventId = Guid.NewGuid().ToString("N"),
        RepoNodeId = "R_node",
        PrNodeId = _node,
        HeadSha = sha ?? _sha1,
        AuthorLogin = _agentLogin,
        AuthorAssociation = "MEMBER",
        UpdatedAt = at,
        PrAuthorLogin = _agentLogin,
        Number = 42,
        Title = title,
        PrState = "open",
        Draft = true,
        BaseRef = "main",
        RepoFullName = "codex-alchemist-dev/Bazaar",
        ReviewerLogin = reviewer,
    };

    private static async Task<(string State, string? Title, string? Head, bool Draft, string? Author, DateTimeOffset Updated, Guid Gc, int Rows)> RowAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT state, title, head_sha, draft, author_login, updated_at, gc_id, (SELECT count(*) FROM github_pr) FROM github_pr WHERE pr_node_id = @n");
        cmd.Parameters.AddWithValue("n", _node);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct));
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2).Trim(), r.GetBoolean(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetGuid(6), (int)r.GetInt64(7));
    }

    [Fact]
    public async Task An_opened_PR_creates_its_GC_and_fills_the_read_model()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghpr", MigrateAsync, ct);
        var factory = new FakeFactory();

        var result = await Service(db.DataSource, factory).ApplyAsync(Opened(_t0, reviewer: _reviewerLogin), ct);

        Assert.NotNull(result);
        Assert.True(result.GcCreated);
        Assert.True(result.Applied);
        Assert.Equal(GitHubPrService.GcIdFor(_node), result.GcId);
        var row = await RowAsync(db.DataSource, ct);
        Assert.Equal(("open", "Add PR service", _sha1, true, _agentLogin, _t0, result.GcId, 1), row);
        Assert.Equal([_owner, _bridge, _charles, _ada], factory.Members[result.GcId]);
        Assert.Equal("codex-alchemist-dev/Bazaar#42 Add PR service", factory.Titles[result.GcId]);
        // The sink's directory now resolves the GC.
        var directory = new PgGitHubGcDirectory(db.DataSource, new GitHubIngressOptions());
        Assert.Equal(result.GcId, await directory.ResolveGcAsync(new GcTarget(GcTargetKind.PullRequest, _node), ct));
    }

    [Fact]
    public async Task Concurrent_callers_and_replays_create_one_GC_and_one_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprcc", MigrateAsync, ct);
        var factory = new FakeFactory();
        var ev = Opened(_t0);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Service(db.DataSource, factory).ApplyAsync(ev, ct)));
        var replay = await Service(db.DataSource, factory).ApplyAsync(ev, ct);

        Assert.Equal(1, factory.Creates);
        Assert.All(results, r => Assert.Equal(GitHubPrService.GcIdFor(_node), r!.GcId));
        Assert.Equal(1, results.Count(r => r!.GcCreated));
        Assert.False(replay!.GcCreated);
        Assert.Equal(1, (await RowAsync(db.DataSource, ct)).Rows);
    }

    [Fact]
    public async Task An_older_event_is_never_applied_over_newer_state_and_a_newer_one_is()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprord", MigrateAsync, ct);
        var service = Service(db.DataSource, new FakeFactory());

        await service.ApplyAsync(Opened(_t0.AddMinutes(10), title: "Newer title", sha: _sha2), ct);
        var older = await service.ApplyAsync(Opened(_t0, title: "Older title", sha: _sha1), ct);
        Assert.False(older!.Applied);
        var closed = Opened(_t0.AddMinutes(20), title: "Final", action: "closed") with { PrState = null, Merged = true, Draft = null };
        var newer = await service.ApplyAsync(closed, ct);
        Assert.True(newer!.Applied);
        var staleClose = await service.ApplyAsync(Opened(_t0.AddMinutes(15), action: "reopened"), ct);

        var row = await RowAsync(db.DataSource, ct);
        Assert.False(staleClose!.Applied);
        Assert.Equal(("merged", "Final", _sha1), (row.State, row.Title, row.Head));
        Assert.Equal(_t0.AddMinutes(20), row.Updated);
    }

    [Fact]
    public async Task A_comment_that_registers_the_PR_does_not_block_an_earlier_opened_event()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprcm", MigrateAsync, ct);
        var service = Service(db.DataSource, new FakeFactory());
        var comment = Opened(_t0.AddMinutes(5)) with { EventName = "issue_comment", Action = "created", Title = null, PrState = null, Draft = null, HeadSha = null, BaseRef = null };

        var first = await service.ApplyAsync(comment, ct);
        var opened = await service.ApplyAsync(Opened(_t0), ct);

        Assert.True(first!.GcCreated);
        Assert.False(first.Applied);
        Assert.True(opened!.Applied);
        var row = await RowAsync(db.DataSource, ct);
        Assert.Equal(("open", "Add PR service", _sha1, _t0), (row.State, row.Title, row.Head, row.Updated));
    }

    [Fact]
    public async Task An_event_without_a_PR_number_or_node_registers_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprnn", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = Service(db.DataSource, factory);

        Assert.Null(await service.ApplyAsync(Opened(_t0) with { Number = null }, ct));
        Assert.Null(await service.ApplyAsync(Opened(_t0) with { PrNodeId = null }, ct));

        Assert.Equal(0, factory.Creates);
        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM github_pr");
        Assert.Equal(0L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task A_later_review_request_adds_the_agent_to_the_existing_GC()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprmb", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = Service(db.DataSource, factory);

        var first = await service.ApplyAsync(Opened(_t0), ct);
        Assert.DoesNotContain(_ada, factory.Members[first!.GcId]);
        await service.ApplyAsync(Opened(_t0.AddMinutes(1), action: "review_requested", reviewer: _reviewerLogin), ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(2), action: "review_requested", reviewer: _reviewerLogin), ct);

        Assert.Equal([_owner, _bridge, _charles, _ada], factory.Members[first.GcId]);
    }

    [Fact]
    public async Task The_sink_decorator_registers_the_PR_before_the_inner_sink_runs()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprsk", MigrateAsync, ct);
        var directory = new PgGitHubGcDirectory(db.DataSource, new GitHubIngressOptions());
        Guid? seen = null;
        var inner = new CallbackSink(async () => seen = await directory.ResolveGcAsync(new GcTarget(GcTargetKind.PullRequest, _node), ct));
        var sink = new GitHubPrGcEventSink(inner, Service(db.DataSource, new FakeFactory()));

        await sink.DeliverAsync(new GitHubEventDelivery(Opened(_t0), "github:poll:1", true, []), ct);

        Assert.Equal(GitHubPrService.GcIdFor(_node), seen);
    }

    private sealed class CallbackSink(Func<Task> callback) : IGitHubEventSink
    {
        public Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken) => callback();
    }

    [Fact]
    public void The_parser_carries_the_number_title_state_and_agent_logins()
    {
        var result = GitHubEventsApiParser.Parse(
            """
            [{"id": "9", "type": "PullRequestEvent", "actor": {"login": "alice"}, "created_at": "2026-11-22T10:00:00Z", "repo": {"name": "acme/app"},
              "payload": {"action": "opened", "pull_request": {"node_id": "PR_9", "number": 7, "title": "T", "state": "open", "draft": true,
                "updated_at": "2026-11-22T10:00:01Z", "head": {"sha": "abc"}, "base": {"ref": "main"}, "user": {"login": "alice"},
                "assignees": [{"login": "a-bazaar[bot]"}], "requested_reviewers": [{"login": "b-bazaar[bot]"}]}}}]
            """, "R_node", new HashSet<string>());

        var e = Assert.Single(result.Events);
        Assert.Equal((7, "T", "open", true, "main", "acme/app"), (e.Number, e.Title, e.PrState, e.Draft, e.BaseRef, e.RepoFullName));
        Assert.Equal(["a-bazaar[bot]", "b-bazaar[bot]"], e.AssigneeLogins);
    }

    [Fact]
    public void The_Events_API_parser_fills_HeadRef_for_same_repo_PRs_and_pushes()
    {
        var result = GitHubEventsApiParser.Parse(
            """
            [{"id": "9", "type": "PullRequestEvent", "actor": {"login": "alice"}, "created_at": "2026-11-22T10:00:00Z", "repo": {"name": "acme/app"},
              "payload": {"action": "synchronize", "pull_request": {"node_id": "PR_9", "number": 7, "state": "open",
                "updated_at": "2026-11-22T10:00:01Z", "head": {"sha": "abc", "ref": "agent/wake", "repo": {"full_name": "acme/app"}}, "base": {"ref": "main", "repo": {"full_name": "acme/app"}}, "user": {"login": "alice"}}}},
             {"id": "10", "type": "PullRequestEvent", "actor": {"login": "alice"}, "created_at": "2026-11-22T10:00:00Z", "repo": {"name": "acme/app"},
              "payload": {"action": "synchronize", "pull_request": {"node_id": "PR_10", "number": 8, "state": "open",
                "updated_at": "2026-11-22T10:00:01Z", "head": {"sha": "abc", "ref": "agent/fork", "repo": {"full_name": "fork/app"}}, "base": {"ref": "main", "repo": {"full_name": "acme/app"}}, "user": {"login": "alice"}}}},
             {"id": "11", "type": "PushEvent", "actor": {"login": "alice"}, "created_at": "2026-11-22T10:00:00Z", "repo": {"name": "acme/app"},
              "payload": {"ref": "refs/heads/agent/wake", "head": "dddddddddddddddddddddddddddddddddddddddd"}}]
            """, "R_node", new HashSet<string>());

        Assert.Equal(["agent/wake", null, "agent/wake"], result.Events.Select(e => e.HeadRef));
    }

    [Fact]
    public async Task The_head_branch_is_stored_with_the_PR_and_kept_when_a_later_event_has_none()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprref", MigrateAsync, ct);
        var service = Service(db.DataSource, new FakeFactory());

        await service.ApplyAsync(Opened(_t0) with { HeadRef = "agent/wake" }, ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(1), action: "edited"), ct); // no branch (for example a fork-shaped payload): the stored one stays

        await using var cmd = db.DataSource.CreateCommand("SELECT head_ref FROM github_pr WHERE pr_node_id = @n");
        cmd.Parameters.AddWithValue("n", _node);
        Assert.Equal("agent/wake", await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Agents_beyond_the_create_cap_are_added_right_after_the_GC_is_created()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprcap", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = new GitHubPrService(db.DataSource, factory, new FakeDirectory(), new GitHubPrGcOptions { MaxAgentMembersAtCreate = 1 }, _owner, _bridge, NullLogger<GitHubPrService>.Instance);

        var result = await service.ApplyAsync(Opened(_t0, reviewer: _reviewerLogin), ct);

        // Charles (the author) fills the one create slot; Ada does not fit and joins at once instead of waiting for her own event.
        Assert.Equal([_owner, _bridge, _charles, _ada], factory.Members[result!.GcId]);
        Assert.Equal(1, factory.Creates);
    }

    [Fact]
    public async Task A_stale_pull_request_event_does_not_add_the_agents_it_names()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprstale", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = Service(db.DataSource, factory);

        var first = await service.ApplyAsync(Opened(_t0), ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(-5), action: "review_requested", reviewer: _reviewerLogin), ct);

        Assert.DoesNotContain(_ada, factory.Members[first!.GcId]);
    }

    [Fact]
    public async Task The_sink_adds_an_agent_that_a_PR_effect_wakes_once_and_ignores_owner_notices_and_external_effects()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprwoke", MigrateAsync, ct);
        var factory = new FakeFactory();
        var sink = new GitHubPrGcEventSink(new CallbackSink(() => Task.CompletedTask), Service(db.DataSource, factory));
        var target = new GcTarget(GcTargetKind.PullRequest, _node);
        var entry = new GcEntryDraft(target, "pr_event", "Comment mentions ada", null);
        var mention = new InboxItemDraft(InboxRecipientRole.Mentioned, _reviewerLogin, InboxKind.Mention, Wakes: true);
        var comment = Opened(_t0.AddMinutes(1)) with { EventName = "issue_comment", Action = "created" };
        await sink.DeliverAsync(new GitHubEventDelivery(Opened(_t0), "github:poll:1", true, []), ct);

        // An external effect and an owner notice add nobody.
        await sink.DeliverAsync(new GitHubEventDelivery(comment, "github:poll:2", true,
            [new MappedGitHubEffect(entry, [mention], GitHubProvenance.External),
             new MappedGitHubEffect(entry, [new InboxItemDraft(InboxRecipientRole.Owner, null, InboxKind.OwnerNotice, Wakes: true)], GitHubProvenance.Member)]), ct);
        var gc = GitHubPrService.GcIdFor(_node);
        Assert.DoesNotContain(_ada, factory.Members[gc]);

        // A member effect that wakes Ada adds her, and doing it again adds no second row.
        var woken = new GitHubEventDelivery(comment, "github:poll:3", true, [new MappedGitHubEffect(entry, [mention], GitHubProvenance.Member)]);
        await sink.DeliverAsync(woken, ct);
        await sink.DeliverAsync(woken, ct);
        Assert.Equal([_owner, _bridge, _charles, _ada], factory.Members[gc]);

        // An older event (Apply false) changes no membership.
        var other = factory.Members[gc].ToList();
        await sink.DeliverAsync(woken with { Apply = false }, ct);
        Assert.Equal(other, factory.Members[gc]);
    }

    [Fact]
    public async Task The_resolved_owner_is_a_member_at_creation_by_default()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprown1", MigrateAsync, ct);
        var factory = new FakeFactory();

        var result = await ServiceWithOwner(db.DataSource, factory, new FakeOwner(_owner)).ApplyAsync(Opened(_t0), ct);

        Assert.Equal([_bridge, _owner, _charles], factory.Members[result!.GcId]);
    }

    [Fact]
    public async Task With_no_owner_yet_the_GC_is_created_without_one_and_the_owner_is_added_at_the_next_event_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprown2", MigrateAsync, ct);
        var factory = new FakeFactory();
        var owner = new FakeOwner(null);
        var service = ServiceWithOwner(db.DataSource, factory, owner);

        var first = await service.ApplyAsync(Opened(_t0), ct);
        Assert.Equal([_bridge, _charles], factory.Members[first!.GcId]);

        owner.Id = _owner;
        await service.ApplyAsync(Opened(_t0.AddMinutes(1), action: "edited"), ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(2), action: "edited"), ct);

        Assert.Equal([_bridge, _charles, _owner], factory.Members[first.GcId]);
    }

    [Fact]
    public async Task OwnerIsMember_false_adds_no_resolved_owner()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprown3", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = ServiceWithOwner(db.DataSource, factory, new FakeOwner(_owner), new GitHubPrGcOptions { OwnerIsMember = false });

        var result = await service.ApplyAsync(Opened(_t0), ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(1), action: "edited"), ct);

        Assert.Equal([_bridge, _charles], factory.Members[result!.GcId]);
    }

    [Fact]
    public async Task The_override_makes_the_owner_the_creator_and_a_member_even_when_OwnerIsMember_is_false()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghprown4", MigrateAsync, ct);
        var factory = new FakeFactory();
        var service = new GitHubPrService(db.DataSource, factory, new FakeDirectory(), new GitHubPrGcOptions { OwnerPrincipalId = _owner, OwnerIsMember = false }, _owner, _bridge, NullLogger<GitHubPrService>.Instance, new FakeOwner(_owner));

        var result = await service.ApplyAsync(Opened(_t0), ct);
        await service.ApplyAsync(Opened(_t0.AddMinutes(1), action: "edited"), ct);

        Assert.Equal([_owner, _bridge, _charles], factory.Members[result!.GcId]);
    }
}
