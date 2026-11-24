using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Decides who may subscribe to one named, non-GC stream such as <c>presence</c> (doc 23). The module that owns the stream
/// registers the gate, so the wire layer needs no reference to it. A stream with no registered gate answers <c>denied</c>,
/// like any unknown stream. A subscription on a gated stream has no replay. The client fetches current state with a request, or
/// the gate supplies an opening event through <see cref="OpenAsync"/>, sent once right after the subscribe response.
/// </summary>
public interface IStreamGate
{
    /// <summary>The stream name this gate decides, for example <see cref="Bazaar.Contracts.Wire.WireStreams.Presence"/>.</summary>
    string Stream { get; }

    /// <summary>True when the principal may subscribe.</summary>
    ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct);

    /// <summary>
    /// The event to send once the subscribe response is queued (a state stream's snapshot), or null for none. It runs on every
    /// subscribe and resubscribe, so a client recovers from a gap by subscribing again. The default sends nothing.
    /// </summary>
    ValueTask<StreamOpening?> OpenAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
        ValueTask.FromResult<StreamOpening?>(null);
}

/// <summary>The first event of a gated stream: its message type and its already encoded payload.</summary>
/// <param name="Type">The wire message type, for example <see cref="Bazaar.Contracts.Wire.WireTypes.KodoEvent"/>.</param>
/// <param name="Payload">The encoded payload bytes.</param>
public sealed record StreamOpening(string Type, ReadOnlyMemory<byte> Payload);
