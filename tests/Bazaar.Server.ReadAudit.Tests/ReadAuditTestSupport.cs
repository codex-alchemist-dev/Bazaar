using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.ReadAudit.Tests;

/// <summary>Keeps every appended entry in memory, in order. Stands in for the Postgres log in unit tests.</summary>
internal sealed class RecordingReadLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }

    /// <summary>Parses the payload of every entry, so tests read fields by name.</summary>
    public IReadOnlyList<JsonObject> Payloads() => Entries.Select(e => JsonNode.Parse(e.Payload)!.AsObject()).ToList();
}

/// <summary>Fixed clock for the read auditor tests.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
