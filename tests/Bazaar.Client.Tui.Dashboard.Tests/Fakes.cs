using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard.Tests;

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeStatus(AdminStatusView view) : IAdminStatusSource
{
    public Task<AdminStatusView> GetAsync(CancellationToken ct = default) => Task.FromResult(view);
}

internal sealed class FakeKodo : IAdminKodoSource
{
    public AdminKodoView View { get; set; } = new(90, [], []);

    public bool Throw { get; set; }

    public Task<AdminKodoView> GetAsync(CancellationToken ct = default) =>
        Throw ? Task.FromException<AdminKodoView>(new InvalidOperationException("down")) : Task.FromResult(View);
}

internal sealed class FakeEvents(params OpsEvent[] events) : IOpsEventSource
{
    public List<OpsEvent> Events { get; } = [.. events];

    public Task<IReadOnlyList<OpsEvent>> ReadRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpsEvent>>([.. Events.OrderByDescending(e => e.At).Take(limit)]);
}

internal sealed class FakeCrashJournal : IOpsCrashJournal
{
    public List<OpsCrashNote> Notes { get; } = [];

    public Task RecordAsync(OpsCrashNote note, CancellationToken ct = default)
    {
        Notes.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OpsCrashNote>> ReadRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpsCrashNote>>([.. Notes.OrderByDescending(n => n.At).Take(limit)]);
}

internal sealed class FakeLogs(params OpsLogLine[] lines) : IOpsLogSource
{
    public OpsLogFilter? LastFilter { get; private set; }

    public Task<IReadOnlyList<OpsLogLine>> TailAsync(OpsLogFilter filter, CancellationToken ct = default)
    {
        LastFilter = filter;
        return Task.FromResult<IReadOnlyList<OpsLogLine>>(
            [.. lines.Where(l => l.Level >= filter.MinLevel && (filter.Module is null || l.Module == filter.Module)).TakeLast(filter.Limit)]);
    }
}

internal sealed class FakeOwnerGate(AdminIdentity? identity) : IOwnerLocalGate
{
    public Task<AdminIdentity?> EnterAsync(CancellationToken ct = default) => Task.FromResult(identity);
}

internal sealed class FakeRedeemer(string goodCode, AdminIdentity identity) : IAdminHandoffRedeemer
{
    private bool _used;

    public Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default)
    {
        if (_used || code != goodCode)
        {
            return Task.FromResult<AdminIdentity?>(null);
        }

        _used = true;
        return Task.FromResult<AdminIdentity?>(identity);
    }
}

internal sealed class FakeAuthorizer(bool allow) : IOpsAuthorizer
{
    public Task<bool> MayUseOpsAsync(AdminIdentity identity, CancellationToken ct = default) => Task.FromResult(allow);
}

internal sealed class FakeSessions : IAdminSessionStore
{
    private readonly Dictionary<string, PanelSession> _live = [];
    private int _next;

    public Task<CreatedPanelSession> CreateAsync(AdminIdentity identity, DateTimeOffset now, CancellationToken ct = default)
    {
        var cookie = "cookie-" + _next++;
        var session = new PanelSession("id-" + cookie, identity.PrincipalId, identity.Kind, identity.DeviceId, now, now, now.AddHours(8));
        _live[cookie] = session;
        return Task.FromResult(new CreatedPanelSession(cookie, session));
    }

    /// <summary>When set, the store revokes everything after this many validations, as an admin revoking the device would.</summary>
    public int? RevokeAfterValidations { get; set; }

    public Task<PanelSession?> ValidateAsync(string cookieValue, DateTimeOffset now, CancellationToken ct = default)
    {
        if (RevokeAfterValidations is { } budget && _validations++ >= budget)
        {
            _live.Clear();
        }

        return Task.FromResult(_live.GetValueOrDefault(cookieValue));
    }

    private int _validations;

    public Task RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        foreach (var key in _live.Where(pair => pair.Value.Id == sessionId).Select(pair => pair.Key).ToList())
        {
            _live.Remove(key);
        }

        return Task.CompletedTask;
    }

    public int LiveCount => _live.Count;

    public void RevokeAll() => _live.Clear();
}

internal sealed class RecordingAudit : IAdminAudit
{
    public List<(string Kind, string Actor, IReadOnlyDictionary<string, string> Fields)> Entries { get; } = [];

    public Task RecordAsync(string kind, string actor, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        Entries.Add((kind, actor, fields));
        return Task.CompletedTask;
    }
}

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

    public static readonly AdminIdentity Owner = new(Guid.Parse("00000000-0000-0000-0000-000000000001"), PrincipalKind.Human, Guid.Parse("00000000-0000-0000-0000-0000000000d1"));

    public static AdminStatusView Status() =>
        new("Bazaar", "0.1.1", "Zuhal", Now.AddDays(-3), 3 * 86400 + 4 * 3600, ["Postgres: ok", "Gateway: ok", "Extra: hidden"]);

    public static ScreenContext Context(int rows = 24, bool color = false) => new(80, rows, new TuiStyle(color), TimeZoneInfo.Utc, Now);

    public static OpsEvent Win(string id, OpsEventKind kind = OpsEventKind.SelfHeal, bool major = false, DateTimeOffset? at = null) =>
        new(id, at ?? Now.AddMinutes(-10), kind, "fixed " + id, major);
}
