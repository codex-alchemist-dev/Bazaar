namespace Bazaar.Wire.Abstractions;

/// <summary>
/// Called once per connection when it ends, on any transport (doc 90 section 9a). Lets a module release
/// state it keeps per connection.
/// </summary>
public interface IWireConnectionObserver
{
    /// <summary>
    /// Runs after the connection closes for any reason (clean, error, timeout, revoke) and after its in-flight
    /// handlers finish. A throwing or slow observer (over 5 s) is logged and skipped by the connection layer.
    /// </summary>
    /// <param name="connectionId">The id the connection layer assigned; the same on every transport.</param>
    /// <param name="cancellationToken">Cancelled when the close budget runs out.</param>
    ValueTask OnClosedAsync(Guid connectionId, CancellationToken cancellationToken);
}
