using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio.Abstractions;

/// <summary>
/// The relay request events the server publishes on <see cref="EngineRelayWire.Stream"/> (doc 02, doc 20) as an async stream.
/// The wire adapter in the Windows head implements it over the subscription; this module never touches the socket.
/// </summary>
public interface IRelayEventSource
{
    /// <summary>Yields events until the connection ends or <paramref name="cancellationToken"/> is cancelled.</summary>
    IAsyncEnumerable<EngineRelayRequestEvent> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Sends the <see cref="EngineRelayWire.RequestType"/> request that answers one relay event. The adapter reuses the envelope id on retry (idempotent request).</summary>
public interface IRelayResultSender
{
    /// <summary>Sends the answer. Throws when it cannot be sent.</summary>
    ValueTask SendAsync(EngineRelayResultRequest result, CancellationToken cancellationToken);
}
