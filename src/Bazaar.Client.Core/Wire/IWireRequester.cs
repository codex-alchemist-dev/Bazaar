namespace Bazaar.Client.Core.Wire;

/// <summary>
/// The one request call a feature needs from the socket client. <see cref="WireClient"/> implements it; a feature takes this
/// interface so its tests can answer requests without a socket.
/// </summary>
public interface IWireRequester
{
    /// <summary>Sends a request and waits for its answer.</summary>
    /// <exception cref="WireRequestException">The server answered with an error.</exception>
    Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default);
}
