namespace Bazaar.Contracts.Wire;

/// <summary>
/// One frame on the socket (docs/design/02-wire-protocol-and-sync.md, Envelope). The payload is opaque
/// to the transport: it holds the codec's encoding of one payload record from this namespace.
/// </summary>
/// <param name="V">Protocol major version.</param>
/// <param name="T">Message type; see <see cref="WireTypes"/>.</param>
/// <param name="K">Request, response, event or error.</param>
/// <param name="Id">Client-chosen id on a request; also its idempotency key.</param>
/// <param name="Re">On a response or error: the <paramref name="Id"/> it answers.</param>
/// <param name="S">Stream name, such as <c>gc:&lt;id&gt;</c>; see <see cref="WireStreams"/>.</param>
/// <param name="N">Sequence on a GC stream.</param>
/// <param name="Ts">Server time, UTC. Absent on frames a client sends.</param>
/// <param name="P">Encoded payload, or empty when the message has none.</param>
public readonly record struct Envelope(
    int V,
    string T,
    EnvelopeKind K,
    string? Id,
    string? Re,
    string? S,
    long? N,
    DateTimeOffset? Ts,
    ReadOnlyMemory<byte> P);
