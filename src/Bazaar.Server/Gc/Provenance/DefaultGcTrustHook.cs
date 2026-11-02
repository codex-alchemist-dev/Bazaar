using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Gc.Provenance;

/// <summary>
/// The shipped taint rules (docs/design/15, "Provenance and taint"):
/// <list type="bullet">
/// <item>External content taints hard: any event whose source is <c>external</c>, and any event a bridge writes, is tainted.</item>
/// <item>Agent text taints softly and only from a tainted source. The writer does not track sessions, so the caller passes
/// the source lineage in <c>TaintSourcesJson</c>; a non-empty lineage marks the event tainted, and no lineage leaves it clean.</item>
/// <item>The owner, the server, and co-members (<c>member-human</c>) are clean.</item>
/// <item>Quarantined content is never delivered to context builders, so it is not tagged as tainted.</item>
/// </list>
/// Taint sources are never invented: without explicit lineage, <c>TaintSourcesJson</c> stays null.
/// </summary>
public sealed class DefaultGcTrustHook : IGcTrustHook
{
    /// <inheritdoc />
    public AppendRequest Resolve(AppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaintSourcesJson is { } lineage && !IsJsonArray(lineage))
            throw new ArgumentException("TaintSourcesJson must be a JSON array.", nameof(request));

        if (request.TaintedAtWrite is not null)
            return request;

        return request with { TaintedAtWrite = IsTainted(request.AuthorKind, request.SourceTrust, request.TaintSourcesJson) };
    }

    private static bool IsTainted(AuthorKind authorKind, SourceTrust sourceTrust, string? lineage) => sourceTrust switch
    {
        SourceTrust.Quarantined => false,
        SourceTrust.External => true,
        SourceTrust.Agent => HasEntries(lineage),
        _ => authorKind == AuthorKind.ExternalBridge,
    };

    private static bool IsJsonArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Only called after IsJsonArray has accepted the text.
    private static bool HasEntries(string? json)
    {
        if (json is null)
            return false;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetArrayLength() > 0;
    }
}
