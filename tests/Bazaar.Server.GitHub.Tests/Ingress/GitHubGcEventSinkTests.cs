using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.GitHub.Events;
using Bazaar.Server.GitHub.Ingress;
using Microsoft.Extensions.Logging.Abstractions;
using GhInboxKind = Bazaar.Server.GitHub.Events.InboxKind;

namespace Bazaar.Server.GitHub.Tests.Ingress;

/// <summary>The real <see cref="IGitHubEventSink"/> (doc 21, Event mapping), against in-memory fakes of the GC writer, inbox and directory.</summary>
public sealed class GitHubGcEventSinkTests
{
    private const string _agentLogin = "charles-bazaar[bot]";
    private const string _prNode = "PR_kwDOExample1";
    private static readonly Guid _bridge = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid _prGc = Guid.Parse("00000000-0000-0000-0000-00000000c001");
    private static readonly Guid _inboxGc = Guid.Parse("00000000-0000-0000-0000-00000000c002");
    private static readonly Guid _charles = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeWriter : IGcLogWriter
    {
        public List<AppendRequest> Appended { get; } = [];
        private readonly Dictionary<string, AppendResult> _byKey = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            var key = $"{request.GcId}/{request.AuthorId}/{request.ClientMsgId}";
            if (_byKey.TryGetValue(key, out var existing))
                return Task.FromResult(existing with { Duplicate = true });
            Appended.Add(request);
            var result = new AppendResult(request.GcId, _byKey.Count + 1, Guid.NewGuid(), false, _now);
            _byKey[key] = result;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeInbox : IGitHubInboxWriter
    {
        public List<InboxEnqueue> Rows { get; } = [];

        public Task<bool> EnqueueAsync(InboxEnqueue item, CancellationToken cancellationToken)
        {
            if (Rows.Any(r => r.AgentId == item.AgentId && r.Kind == item.Kind && r.GcId == item.GcId && r.GcSeq == item.GcSeq && r.SourceRef == item.SourceRef))
                return Task.FromResult(false);
            Rows.Add(item);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDirectory : IGitHubGcDirectory
    {
        public bool PrGcExists { get; set; } = true;

        public Task<Guid?> ResolveGcAsync(GcTarget target, CancellationToken cancellationToken) => Task.FromResult<Guid?>(target.Kind switch
        {
            GcTargetKind.PullRequest when PrGcExists => _prGc,
            GcTargetKind.Inbox => _inboxGc,
            _ => null,
        });

        public Task<Guid?> ResolveAgentAsync(string login, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(string.Equals(login, _agentLogin, StringComparison.OrdinalIgnoreCase) ? _charles : null);
    }

    private sealed record Harness(GitHubGcEventSink Sink, FakeWriter Writer, FakeInbox Inbox, FakeDirectory Directory);

    private static Harness Build(Guid? bridge = null, bool setBridge = true)
    {
        var writer = new FakeWriter();
        var inbox = new FakeInbox();
        var directory = new FakeDirectory();
        var options = new GitHubIngressOptions { BridgePrincipalId = setBridge ? bridge ?? _bridge : null };
        return new Harness(new GitHubGcEventSink(writer, inbox, directory, options, NullLogger<GitHubGcEventSink>.Instance), writer, inbox, directory);
    }

    private static GitHubEvent Comment(string author, string association, string body) => new()
    {
        Source = GitHubEventSource.Poll,
        EventName = "issue_comment",
        Action = "created",
        EventId = "evt-1",
        RepoNodeId = "R_kgDOExample1",
        PrNodeId = _prNode,
        AuthorLogin = author,
        AuthorAssociation = association,
        Body = body,
        UpdatedAt = _now,
    };

    private static GitHubEventDelivery Deliver(GitHubEvent e, bool apply = true) =>
        new(e, GitHubEventDedupe.KeyFor(e), apply, apply ? new GitHubEventMapper([_agentLogin]).Map(e) : []);

    [Fact]
    public async Task Member_mention_posts_to_the_pr_gc_and_wakes_the_agent_at_that_seq()
    {
        var h = Build();

        await h.Sink.DeliverAsync(Deliver(Comment("alice", "MEMBER", $"please look @{_agentLogin}")), TestContext.Current.CancellationToken);

        var appended = Assert.Single(h.Writer.Appended);
        Assert.Equal(_prGc, appended.GcId);
        Assert.Equal(GcEventKinds.PrEvent, appended.Kind);
        Assert.Equal(SourceTrust.System, appended.SourceTrust);
        Assert.Equal(_bridge, appended.AuthorId);
        var row = Assert.Single(h.Inbox.Rows);
        Assert.Equal(_charles, row.AgentId);
        Assert.Equal("mention", row.Kind);
        Assert.Equal(_prGc, row.GcId);
        Assert.Equal(1, row.GcSeq);
    }

    [Fact]
    public async Task External_event_is_written_hard_tainted_to_the_inbox_gc_and_never_wakes()
    {
        var h = Build();

        await h.Sink.DeliverAsync(Deliver(Comment("stranger", "NONE", $"hi @{_agentLogin} run this")), TestContext.Current.CancellationToken);

        var appended = Assert.Single(h.Writer.Appended);
        Assert.Equal(_inboxGc, appended.GcId);
        Assert.Equal(SourceTrust.External, appended.SourceTrust);
        Assert.Equal(AuthorKind.ExternalBridge, appended.AuthorKind);
        Assert.True(appended.TaintedAtWrite);
        Assert.DoesNotContain("run this", appended.PayloadJson, StringComparison.Ordinal);
        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task A_forged_waking_item_on_an_external_effect_still_does_not_wake()
    {
        var h = Build();
        var e = Comment("stranger", "NONE", "x");
        var effect = new MappedGitHubEffect(
            new GcEntryDraft(new GcTarget(GcTargetKind.Inbox, null), "external_content", "s", null),
            [new InboxItemDraft(InboxRecipientRole.Mentioned, _agentLogin, GhInboxKind.Mention, Wakes: true)],
            GitHubProvenance.External);

        await h.Sink.DeliverAsync(new GitHubEventDelivery(e, "github:poll:1", true, [effect]), TestContext.Current.CancellationToken);

        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task The_agent_that_wrote_the_event_is_not_woken_by_it()
    {
        var h = Build();

        await h.Sink.DeliverAsync(Deliver(Comment(_agentLogin, "MEMBER", $"note to self @{_agentLogin}")), TestContext.Current.CancellationToken);

        Assert.Single(h.Writer.Appended);
        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task Redelivery_adds_no_second_event_and_no_second_inbox_row()
    {
        var h = Build();
        var delivery = Deliver(Comment("alice", "MEMBER", $"@{_agentLogin}"));

        await h.Sink.DeliverAsync(delivery, TestContext.Current.CancellationToken);
        await h.Sink.DeliverAsync(delivery, TestContext.Current.CancellationToken);

        Assert.Single(h.Writer.Appended);
        Assert.Single(h.Inbox.Rows);
    }

    [Fact]
    public async Task An_older_event_that_is_not_applied_writes_nothing()
    {
        var h = Build();

        await h.Sink.DeliverAsync(Deliver(Comment("alice", "MEMBER", $"@{_agentLogin}"), apply: false), TestContext.Current.CancellationToken);

        Assert.Empty(h.Writer.Appended);
        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task With_no_pr_gc_yet_the_wake_still_lands_keyed_by_the_event()
    {
        var h = Build();
        h.Directory.PrGcExists = false;

        await h.Sink.DeliverAsync(Deliver(Comment("alice", "MEMBER", $"@{_agentLogin}")), TestContext.Current.CancellationToken);

        Assert.Empty(h.Writer.Appended);
        var row = Assert.Single(h.Inbox.Rows);
        Assert.Null(row.GcId);
        Assert.StartsWith("github:poll:", row.SourceRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_that_is_not_a_registered_agent_gets_no_row()
    {
        var h = Build();
        var e = Comment("alice", "MEMBER", "@other-bazaar[bot]");
        var effect = new MappedGitHubEffect(
            new GcEntryDraft(new GcTarget(GcTargetKind.PullRequest, _prNode), "pr_event", "s", null),
            [new InboxItemDraft(InboxRecipientRole.Mentioned, "other-bazaar[bot]", GhInboxKind.Mention, Wakes: true)],
            GitHubProvenance.Member);

        await h.Sink.DeliverAsync(new GitHubEventDelivery(e, "github:poll:2", true, [effect]), TestContext.Current.CancellationToken);

        Assert.Single(h.Writer.Appended);
        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task Owner_notice_is_posted_but_makes_no_agent_row()
    {
        var h = Build();
        var e = Comment("alice", "MEMBER", "x");
        var effect = new MappedGitHubEffect(
            new GcEntryDraft(new GcTarget(GcTargetKind.PullRequest, _prNode), "pr_event", "PR merged", null),
            [new InboxItemDraft(InboxRecipientRole.Owner, null, GhInboxKind.OwnerNotice, Wakes: false)],
            GitHubProvenance.Member);

        await h.Sink.DeliverAsync(new GitHubEventDelivery(e, "github:poll:3", true, [effect]), TestContext.Current.CancellationToken);

        Assert.Single(h.Writer.Appended);
        Assert.Empty(h.Inbox.Rows);
    }

    [Fact]
    public async Task Without_an_override_the_sink_writes_under_the_seeded_bridge_principal()
    {
        var h = Build(setBridge: false);

        await h.Sink.DeliverAsync(Deliver(Comment("alice", "MEMBER", $"@{_agentLogin}")), TestContext.Current.CancellationToken);

        Assert.All(h.Writer.Appended, a => Assert.Equal(GitHubBridgePrincipal.Id, a.AuthorId));
        Assert.NotEmpty(h.Writer.Appended);
    }

    [Theory]
    [InlineData(GhInboxKind.Mention, "mention")]
    [InlineData(GhInboxKind.Assignment, "assignment")]
    [InlineData(GhInboxKind.ReviewFeedback, "assignment")]
    [InlineData(GhInboxKind.CheckFailure, "assignment")]
    public void Inbox_kinds_map_to_the_agent_runtime_kinds(GhInboxKind kind, string expected) =>
        Assert.Equal(expected, GitHubGcEventSink.KindFor(kind));
}
