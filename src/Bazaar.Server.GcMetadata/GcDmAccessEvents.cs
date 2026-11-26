using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.GcMetadata;

/// <summary>Writes the DM access <c>system</c> events through the one GC log writer (doc 01: the request and the decision are <c>system</c> events).</summary>
public sealed class GcDmAccessEvents(IGcLogWriter writer) : IDmAccessEvents
{
    /// <inheritdoc />
    public async Task<long> AppendAsync(Guid gcId, string kind, Guid requestId, Guid requester, Guid actor, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["type"] = kind,
            ["request_id"] = requestId.ToString("D"),
            ["requester"] = requester.ToString("D"),
            ["actor"] = actor.ToString("D"),
        };

        // The key is per request and kind, so a retry returns the first event and its seq rather than a second one.
        var request = new AppendRequest(gcId, GcEventKinds.System, AuthorKind.System, Guid.Empty, SourceTrust.System, $"dm-access:{kind}:{requestId:N}")
        {
            PayloadJson = payload.ToJsonString(),
        };
        var result = await writer.AppendAsync(request, ct).ConfigureAwait(false);
        return result.Seq;
    }
}
