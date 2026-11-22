using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Decides who may subscribe to one named, non-GC stream such as <c>presence</c> (doc 23). The module that owns the stream
/// registers the gate, so the wire layer needs no reference to it. A stream with no registered gate answers <c>denied</c>,
/// like any unknown stream. A subscription on a gated stream has no replay; the client fetches current state with a request.
/// </summary>
public interface IStreamGate
{
    /// <summary>The stream name this gate decides, for example <see cref="Bazaar.Contracts.Wire.WireStreams.Presence"/>.</summary>
    string Stream { get; }

    /// <summary>True when the principal may subscribe.</summary>
    ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct);
}
