using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin.Http;

namespace Bazaar.Server.WebAdmin.Tests.Http.Fakes;

/// <summary>Permission checker backed by a per-principal capability set at instance scope.</summary>
internal sealed class FakeChecker : IPermissionChecker
{
    public ConcurrentDictionary<Guid, HashSet<string>> Held { get; } = new();

    public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
        Task.FromResult(scope == Scope.Instance && Held.TryGetValue(principalId, out var caps) && caps.Contains(capability.Value));
}

internal sealed class FakeSessionStore : IAdminSessionStore
{
    private readonly ConcurrentDictionary<string, PanelSession> _byCookie = new();
    private int _next;

    public List<string> Revoked { get; } = [];

    public (string Cookie, Guid PrincipalId) Seed(PrincipalKind kind)
    {
        var cookie = "seeded-" + Interlocked.Increment(ref _next);
        var s = Make(cookie, new AdminIdentity(Guid.NewGuid(), kind, Guid.NewGuid()), DateTimeOffset.UnixEpoch);
        _byCookie[cookie] = s;
        return (cookie, s.PrincipalId);
    }

    public Task<CreatedPanelSession> CreateAsync(AdminIdentity identity, DateTimeOffset now, CancellationToken ct = default)
    {
        var cookie = "created-" + Interlocked.Increment(ref _next);
        var s = Make(cookie, identity, now);
        _byCookie[cookie] = s;
        return Task.FromResult(new CreatedPanelSession(cookie, s));
    }

    public Task<PanelSession?> ValidateAsync(string cookieValue, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(_byCookie.TryGetValue(cookieValue, out var s) ? s : null);

    public Task RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        Revoked.Add(sessionId);
        foreach (var kv in _byCookie.Where(kv => kv.Value.Id == sessionId).ToList())
        {
            _byCookie.TryRemove(kv.Key, out _);
        }

        return Task.CompletedTask;
    }

    private static PanelSession Make(string cookie, AdminIdentity i, DateTimeOffset now) =>
        new("hash-" + cookie, i.PrincipalId, i.Kind, i.DeviceId, now, now, now.AddHours(8));
}

/// <summary>Records both the panel's own audit entries and the gate's (<see cref="IAuditLog"/>) in one list.</summary>
internal sealed class FakeAudit : IAdminAudit, IAuditLog
{
    public ConcurrentQueue<(string Kind, string Actor, IReadOnlyDictionary<string, string> Fields)> Entries { get; } = new();

    public bool Throw { get; set; }

    public Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("audit down");
        }

        Entries.Enqueue((kind, actor, fields));
        return Task.CompletedTask;
    }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(e.Payload);
        var fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        return RecordAsync(e.Kind, e.Actor, fields, ct);
    }

    public IReadOnlyList<(string Kind, string Actor, IReadOnlyDictionary<string, string> Fields)> Of(string kind) =>
        Entries.Where(e => e.Kind == kind).ToList();
}

internal sealed class FakeRedeemer : IAdminHandoffRedeemer
{
    public Dictionary<string, AdminIdentity> Codes { get; } = new();

    public Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(Codes.Remove(code, out var id) ? id : null);
}

internal sealed class FakeAssets : IAdminAssetProvider
{
    public Dictionary<string, (byte[] Content, string Type)> Files { get; } = new(StringComparer.Ordinal)
    {
        ["index.html"] = ("<html></html>"u8.ToArray(), "text/html; charset=utf-8"),
        ["assets/panel.js"] = ("let a=1;"u8.ToArray(), "text/javascript"),
    };

    public List<string> Asked { get; } = [];

    public bool TryGet(string path, out byte[] content, out string contentType)
    {
        Asked.Add(path);
        if (Files.TryGetValue(path, out var f))
        {
            (content, contentType) = f;
            return true;
        }

        content = [];
        contentType = string.Empty;
        return false;
    }
}

internal sealed class FakeStatus : IAdminStatusSource
{
    public Task<AdminStatusView> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new AdminStatusView("Bazaar", "0.1.0", "Zuhal", DateTimeOffset.UnixEpoch, 5, ["p1"], [new AdminQueueDepth("jobs", 2)], new AdminHealthSummary("degraded", [new AdminHealthCheck("mail", "degraded", "slow")])));
}

internal sealed class FakeKodo : IAdminKodoSource
{
    public Task<AdminKodoView> GetAsync(CancellationToken ct = default) => Task.FromResult(new AdminKodoView(90, [], []));
}

internal sealed class FakeUsers : IAdminUserDirectory
{
    public Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminUser>>([new AdminUser(Guid.Empty, "Alice", "owner", false, false)]);
}

internal sealed class FakeDevices : IAdminDeviceDirectory
{
    public Task<IReadOnlyList<AdminDevice>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminDevice>>([new AdminDevice(Guid.Empty, Guid.Empty, "laptop", DateTimeOffset.UnixEpoch, false)]);
}

internal sealed class FakeAuditReader : IAdminAuditReader
{
    public long? LastBefore { get; private set; }

    public int LastLimit { get; private set; }

    public Task<AdminAuditPage> ReadAsync(long? before, int limit, CancellationToken ct = default)
    {
        LastBefore = before;
        LastLimit = limit;
        return Task.FromResult(new AdminAuditPage([new AdminAuditRow(7, DateTimeOffset.UnixEpoch, "a1", "k", "s")], 6));
    }
}

internal sealed class FakeMemberRoster : IAdminMemberRoster
{
    public string? LastKind { get; private set; }

    public int LastLimit { get; private set; }

    public int LastOffset { get; private set; }

    public Task<AdminMemberPage> ListAsync(string? kind, int limit, int offset, CancellationToken ct = default)
    {
        (LastKind, LastLimit, LastOffset) = (kind, limit, offset);
        return Task.FromResult(new AdminMemberPage(
            [
                new AdminMember(Guid.Empty, "Ada", "agent", "agent", "active", DateTimeOffset.UnixEpoch, "claude-code/sonnet", 1200),
                new AdminMember(Guid.Empty, "Dom", "human", "owner", "active", null, null, 0),
            ],
            2,
            null));
    }
}

internal sealed class FakeApprovals : IAdminApprovalSource
{
    public Task<IReadOnlyList<AdminApproval>> ListPendingAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminApproval>>([new AdminApproval("ap1", "merge", "agent", DateTimeOffset.UnixEpoch)]);
}

internal sealed class FakeModules : IAdminModuleSelection
{
    public long CurrentRevision { get; set; } = 3;

    public bool RebuildStarts { get; set; } = true;

    public int CommitCalls { get; private set; }

    public int RebuildCalls { get; private set; }

    public string? LastIdempotencyKey { get; private set; }

    public Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default) =>
        Task.FromResult(new AdminModuleCatalogue(CurrentRevision, [new AdminModuleInfo("m1", "Mod", "1.0.0", "stable", [], true)]));

    public Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminBuildRecord>>([new AdminBuildRecord("b1", "succeeded", DateTimeOffset.UnixEpoch, null, null, 1)]);

    public Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default) =>
        Task.FromResult(new AdminSelectionPreview(true, [], selectedModuleIds, []));

    public Task<AdminSelectionCommit> CommitAsync(Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default)
    {
        CommitCalls++;
        return Task.FromResult(expectedRevision == CurrentRevision
            ? new AdminSelectionCommit(true, ++CurrentRevision, [])
            : new AdminSelectionCommit(false, CurrentRevision, []));
    }

    public Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default)
    {
        RebuildCalls++;
        LastIdempotencyKey = idempotencyKey;
        return Task.FromResult(RebuildStarts ? new AdminRebuildStart(true, "b2", null) : new AdminRebuildStart(false, null, "running"));
    }
}
