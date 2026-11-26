using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.PrivacyAudit;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.BrowserPool.Audit;

/// <summary>
/// Writes browser pool audit rows to the audit log (docs/design/59, section 7 and "Browser pool isolation"; docs/design/15,
/// "Audit log and kill switch"). A row carries the agent id, slot number, display mode, timestamps and enum names, plus
/// the pool's own fixed detail text (control characters removed, cut to <see cref="PoolAuditOptions.MaxDetailLength"/>). It never
/// carries a URL, page content, proxy address or credentials: the pool's session record holds none, and this sink reads nothing
/// else. <see cref="IPoolAuditSink"/> is synchronous, so each write waits for the log, bounded by
/// <see cref="PoolAuditOptions.WriteTimeoutSeconds"/>.
/// </summary>
/// <remarks>
/// The port says implementations must not throw, and the pool swallows any exception from a sink so that an audit failure cannot
/// stop a close or the kill switch. So unlike the display sink, this one cannot make a pool call fail when its row was not written:
/// a failed, timed-out or missing-log write is dropped and counted in <see cref="FailedWrites"/>. The kinds and the actor form
/// <c>agent:{id}</c> are this module's choice; docs/design/15 names no pool audit kinds.
/// </remarks>
public sealed class AuditLogPoolAuditSink : IPoolAuditSink
{
    /// <summary>Kind of a row for a session that was opened.</summary>
    public const string OpenedKind = "browser-pool.opened";

    /// <summary>Kind of a row for a session that was closed.</summary>
    public const string ClosedKind = "browser-pool.closed";

    /// <summary>Kind of a row for an open that was refused or failed.</summary>
    public const string RefusedKind = "browser-pool.refused";

    /// <summary>Kind of a row for a slot taken out of use.</summary>
    public const string SlotQuarantinedKind = "browser-pool.slot_quarantined";

    private readonly IAuditLog? _log;
    private readonly TimeProvider _time;
    private readonly PoolAuditOptions _options;
    private long _failedWrites;

    /// <summary>Creates the sink. A <see langword="null"/> log makes every write fail (and be counted).</summary>
    public AuditLogPoolAuditSink(IAuditLog? log, TimeProvider time, PoolAuditOptions options)
    {
        _log = log;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>How many rows could not be written since the sink was created.</summary>
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    /// <inheritdoc />
    public void Opened(PoolSession session)
    {
        if (session is null)
            return;
        JsonObject payload = SessionPayload(session);
        payload["expires_at"] = session.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        Write(OpenedKind, session.AgentId, payload);
    }

    /// <inheritdoc />
    public void Closed(PoolSession session, PoolCloseReason reason)
    {
        if (session is null)
            return;
        JsonObject payload = SessionPayload(session);
        payload["reason"] = reason.ToString();
        Write(ClosedKind, session.AgentId, payload);
    }

    /// <inheritdoc />
    public void Refused(string agentId, PoolFailure failure, string detail)
    {
        JsonObject payload = new()
        {
            ["agent_id"] = Clean(agentId),
            ["failure"] = failure.ToString(),
            ["detail"] = Clean(detail),
        };
        Write(RefusedKind, agentId, payload);
    }

    /// <inheritdoc />
    public void SlotQuarantined(int slot, string sessionId, string agentId, string detail)
    {
        JsonObject payload = new()
        {
            ["agent_id"] = Clean(agentId),
            ["slot"] = slot,
            ["detail"] = Clean(detail),
        };
        Write(SlotQuarantinedKind, agentId, payload);
    }

    private JsonObject SessionPayload(PoolSession session) => new()
    {
        ["agent_id"] = Clean(session.AgentId),
        ["slot"] = session.Slot,
        ["mode"] = session.Display.ToString(),
        ["started_at"] = session.StartedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>Removes control characters and cuts the text to the configured length.</summary>
    private string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, _options.MaxDetailLength));
        foreach (char c in value)
        {
            if (sb.Length >= _options.MaxDetailLength)
                break;
            if (!char.IsControl(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    private void Write(string kind, string? agentId, JsonObject payload)
    {
        try
        {
            if (_log is null)
                throw new InvalidOperationException("No audit log is registered.");
            AuditPayloadGuard.Ensure(payload);
            var entry = new AuditEntry(_time.GetUtcNow(), $"agent:{Clean(agentId)}", kind, payload.ToJsonString());
            _log.AppendAsync(entry, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(_options.WriteTimeoutSeconds), _time)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // The port forbids throwing and the pool swallows it anyway. The exception is not kept: a log provider's message could
            // echo the payload.
            Interlocked.Increment(ref _failedWrites);
        }
    }
}
