using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.DevSandbox;

/// <summary>Writes a run's outcome to the instance audit log. Never carries action payloads or canary values.</summary>
public sealed class AuditLogSandboxResultSink
{
    private readonly IAuditLog _log;

    /// <summary>Creates the sink.</summary>
    /// <param name="log">The audit log.</param>
    public AuditLogSandboxResultSink(IAuditLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Appends one <c>sandbox.run</c> entry.</summary>
    /// <param name="r">The run result.</param>
    /// <param name="actor">Who started the run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the entry is committed.</returns>
    public Task WriteAsync(SandboxRunResult r, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentException.ThrowIfNullOrEmpty(actor);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("runId", r.RunId);
            w.WriteString("verdict", r.Verdict.ToString());
            w.WriteNumber("eventCount", r.EventCount);
            w.WriteString("auditHeadHash", r.AuditHeadHash);
            w.WriteNumber("violationCount", r.Violations.Count);
            w.WriteEndObject();
        }

        var entry = new AuditEntry(TimeProvider.System.GetUtcNow(), actor, "sandbox.run", Encoding.UTF8.GetString(ms.ToArray()));
        return _log.AppendAsync(entry, ct);
    }
}
