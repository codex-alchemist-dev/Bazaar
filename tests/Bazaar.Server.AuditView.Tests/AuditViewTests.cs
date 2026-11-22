using System.Security.Cryptography;
using System.Text;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditView.Tests;

internal static class ViewExtensions
{
    public static Task<AuditViewPage> Run(this IAuditViewService s, AuditViewer v, AuditViewQuery q) =>
        s.ViewAsync(v, q, TestContext.Current.CancellationToken);
}

public sealed class AuditViewTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private sealed class FakePermissions : IPermissionChecker
    {
        private readonly HashSet<(Guid, string, ScopeKind)> _grants = [];

        public FakePermissions Grant(Guid p, Capability c, ScopeKind s)
        {
            _grants.Add((p, c.Value, s));
            return this;
        }

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
        {
            // Mirrors the real checker: an Instance grant covers all, a Company grant covers non-instance scopes.
            var ok = _grants.Contains((principalId, capability.Value, ScopeKind.Instance))
                || (scope.Kind != ScopeKind.Instance && _grants.Contains((principalId, capability.Value, ScopeKind.Company)));
            return Task.FromResult(ok);
        }
    }

    private sealed class FakeLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("append failed");
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    /// <summary>In-memory read-only source over a hash-chained list.</summary>
    private sealed class FakeSource : IAuditViewSource
    {
        public List<AuditSourceRow> Rows { get; } = [];

        public Task<IReadOnlyList<AuditSourceRow>> ReadAsync(AuditSourceQuery q, CancellationToken ct = default)
        {
            IReadOnlyList<AuditSourceRow> r = Rows.Where(x => x.Seq > q.AfterSeq
                && (q.ToSeq is null || x.Seq <= q.ToSeq)
                && (q.From is null || x.Ts >= q.From) && (q.To is null || x.Ts <= q.To))
                .OrderBy(x => x.Seq).Take(q.Limit).ToList();
            return Task.FromResult(r);
        }

        public string ChainDigest()
        {
                var sb = new StringBuilder();
            foreach (var x in Rows)
                sb.Append(x.Seq).Append('|').Append(x.Ts.UtcTicks).Append('|').Append(x.Actor).Append('|').Append(x.Kind).Append('|').Append(x.Payload).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }

    private readonly FakeSource _source = new();
    private readonly FakeLog _log = new();
    private readonly AuditViewOptions _options = new();

    private AuditViewService Service()
    {
        var perms = new FakePermissions()
            .Grant(Owner, Capabilities.Approve, ScopeKind.Instance)
            .Grant(Reviewer, new Capability("audit.view.security"), ScopeKind.Company)
            .Grant(AgentId, Capabilities.Approve, ScopeKind.Instance)
            .Grant(AgentId, new Capability("audit.view.security"), ScopeKind.Company);
        return new AuditViewService(_source, new AuditViewScopeResolver(perms, _options), _log, _options, TimeProvider.System);
    }

    private void Seed(int n = 6)
    {
        string[] kinds = ["security.signin", "data.read", "approval.granted", "gc.message", "module.permissions.approved", "kanban.moved"];
        for (var i = 1; i <= n; i++)
            _source.Rows.Add(new AuditSourceRow(i, DateTimeOffset.UnixEpoch.AddMinutes(i), "actor", kinds[(i - 1) % kinds.Length], $"{{\"n\":{i}}}"));
    }

    [Fact]
    public async Task Owner_sees_all()
    {
        Seed();
        var page = await Service().Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery());
        Assert.Equal(AuditViewAccess.Granted, page.Access);
        Assert.Equal(6, page.Rows.Count);
        Assert.Contains(page.Rows, r => r.Category == AuditViewCategories.Other);
    }

    [Fact]
    public async Task Reviewer_sees_only_security()
    {
        Seed();
        var page = await Service().Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery());
        Assert.Equal(AuditViewAccess.Granted, page.Access);
        Assert.Equal([1L, 3L, 5L], page.Rows.Select(r => r.Seq).ToArray());
        Assert.All(page.Rows, r => Assert.Equal(AuditViewCategories.Security, r.Category));
    }

    [Fact]
    public async Task Reviewer_cannot_widen_with_kind_filter()
    {
        Seed();
        var page = await Service().Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery(Kinds: ["data.read", "gc."]));
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task Agent_sees_nothing_even_with_grants()
    {
        Seed();
        var page = await Service().Run(new AuditViewer(AgentId, PrincipalKind.Agent), new AuditViewQuery());
        Assert.Equal(AuditViewAccess.Denied, page.Access);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task Unknown_principal_and_service_are_denied()
    {
        Seed();
        var s = Service();
        Assert.Equal(AuditViewAccess.Denied, (await s.Run(new AuditViewer(Stranger, PrincipalKind.Human), new AuditViewQuery())).Access);
        Assert.Equal(AuditViewAccess.Denied, (await s.Run(new AuditViewer(Owner, PrincipalKind.Service), new AuditViewQuery())).Access);
        Assert.Equal(AuditViewAccess.Denied, (await s.Run(new AuditViewer(Guid.Empty, PrincipalKind.Human), new AuditViewQuery())).Access);
    }

    [Fact]
    public async Task Resolver_failure_denies()
    {
        Seed();
        var svc = new AuditViewService(_source, new AuditViewScopeResolver(new ThrowingPermissions(), _options), _log, _options, TimeProvider.System);
        Assert.Equal(AuditViewAccess.Denied, (await svc.Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery())).Access);
    }

    private sealed class ThrowingPermissions : IPermissionChecker
    {
        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
            throw new InvalidOperationException("store down");
    }

    [Fact]
    public async Task Paging_walks_all_rows_without_gaps_or_repeats()
    {
        Seed(25);
        var s = Service();
        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await s.Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery(Cursor: cursor, PageSize: 10));
            seen.AddRange(page.Rows.Select(r => r.Seq));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Paging_with_narrow_scope_filters_before_counting()
    {
        Seed(30);
        var s = Service();
        var page = await s.Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery(PageSize: 4));
        Assert.Equal(4, page.Rows.Count);
        Assert.NotNull(page.NextCursor);
        var next = await s.Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery(Cursor: page.NextCursor, PageSize: 100));
        Assert.Equal(15, page.Rows.Count + next.Rows.Count);
        Assert.Null(next.NextCursor);
    }

    [Fact]
    public async Task Range_and_kind_filters_apply()
    {
        Seed();
        var s = Service();
        var owner = new AuditViewer(Owner, PrincipalKind.Human);
        var bySeq = await s.Run(owner, new AuditViewQuery(FromSeq: 2, ToSeq: 4));
        Assert.Equal([2L, 3L, 4L], bySeq.Rows.Select(r => r.Seq).ToArray());
        var byKind = await s.Run(owner, new AuditViewQuery(Kinds: ["gc.message", "approval."]));
        Assert.Equal([3L, 4L], byKind.Rows.Select(r => r.Seq).ToArray());
        var byTime = await s.Run(owner, new AuditViewQuery(From: DateTimeOffset.UnixEpoch.AddMinutes(5)));
        Assert.Equal([5L, 6L], byTime.Rows.Select(r => r.Seq).ToArray());
        Assert.Equal(AuditViewAccess.InvalidQuery, (await s.Run(owner, new AuditViewQuery(Cursor: "abc"))).Access);
        Assert.Equal(AuditViewAccess.InvalidQuery, (await s.Run(owner, new AuditViewQuery(FromSeq: 5, ToSeq: 2))).Access);
    }

    [Fact]
    public async Task Ciphertext_payload_passes_through_unchanged()
    {
        const string payload = "{\"name\":\"enc:v1:AAECAwQFBgc=\"}";
        _source.Rows.Add(new AuditSourceRow(1, DateTimeOffset.UnixEpoch, "a", "security.signin", payload));
        var page = await Service().Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery());
        Assert.Equal(payload, page.Rows.Single().Payload);
    }

    [Fact]
    public async Task Every_view_is_audited_including_refusals()
    {
        Seed();
        var s = Service();
        await s.Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery());
        await s.Run(new AuditViewer(AgentId, PrincipalKind.Agent), new AuditViewQuery());
        Assert.Equal(2, _log.Entries.Count);
        Assert.All(_log.Entries, e => Assert.Equal("audit.view", e.Kind));
        Assert.Equal(Owner.ToString("D"), _log.Entries[0].Actor);
        Assert.Contains("\"outcome\":\"granted\"", _log.Entries[0].Payload);
        Assert.Contains("\"returned\":6", _log.Entries[0].Payload);
        Assert.Contains("\"outcome\":\"denied\"", _log.Entries[1].Payload);
    }

    [Fact]
    public async Task Failed_audit_append_shows_nothing()
    {
        Seed();
        _log.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery()));
    }

    [Fact]
    public async Task Chain_is_untouched_by_views()
    {
        Seed();
        var before = _source.ChainDigest();
        var s = Service();
        await s.Run(new AuditViewer(Owner, PrincipalKind.Human), new AuditViewQuery());
        await s.Run(new AuditViewer(Reviewer, PrincipalKind.Human), new AuditViewQuery());
        Assert.Equal(before, _source.ChainDigest());
        Assert.Equal(6, _source.Rows.Count);
    }

    [Fact]
    public void Public_surface_has_no_mutation_api()
    {
        foreach (var t in new[] { typeof(IAuditViewService), typeof(IAuditViewSource), typeof(IAuditViewScopeResolver) })
        {
            foreach (var m in t.GetMethods())
            {
                Assert.Matches("^(View|Read|Resolve)Async$", m.Name);
            }
        }
    }

    [Fact]
    public void Options_validate_and_map_categories()
    {
        _options.Validate();
        Assert.Equal("security", _options.CategoryOf("security.x"));
        Assert.Equal("other", _options.CategoryOf("gc.message"));
        Assert.Throws<ArgumentException>(() => new AuditViewOptions { OwnerCapability = "Bad Name" }.Validate());
        Assert.Throws<ArgumentException>(() => new AuditViewOptions { MaxPageSize = 10, DefaultPageSize = 20 }.Validate());
    }
}
