using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Adapts the host audit log (doc 15 <c>IAuditLog</c>) to the KEK audit seam. Each entry carries the event kind and a payload of
/// ids, counts and custody class only, never key material. <see cref="CustodyEventKind"/> is the start-up record of which
/// provider holds the KEK and how strong that custody is.
/// </summary>
public sealed class AuditLogKekSink(IAuditLog log, string actor = "kek", TimeProvider? clock = null) : IKekAuditSink
{
    /// <summary>The audit kind recording the KEK provider and custody class at start-up.</summary>
    public const string CustodyEventKind = "identity.kek.custody";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task RecordAsync(string kind, string payloadJson, CancellationToken ct) =>
        log.AppendAsync(new AuditEntry(_clock.GetUtcNow(), actor, kind, payloadJson), ct);

    /// <summary>Records the provider kind and custody class (for example <c>hardware</c> or <c>virtual</c>). No key material.</summary>
    public Task RecordCustodyAsync(string providerKind, string custodyClass, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(custodyClass);
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", providerKind);
            writer.WriteString("custody", custodyClass);
            writer.WriteEndObject();
        }

        var payload = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return RecordAsync(CustodyEventKind, payload, ct);
    }
}
