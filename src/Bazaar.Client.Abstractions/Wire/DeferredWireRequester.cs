using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Abstractions.Wire;

/// <summary>
/// An <see cref="IWireRequester"/> whose real requester arrives later. A head builds its panes before the chat session
/// exists, so a pane's wire source takes this and the head calls <see cref="Set"/> once the session has started. Until then
/// every request fails with a <see cref="WireRequestException"/> (code <see cref="NotConnectedCode"/>), which panes treat
/// as a refusal and hide themselves instead of crashing.
/// </summary>
public sealed class DeferredWireRequester : IWireRequester
{
    /// <summary>The error code a request carries while no requester is set.</summary>
    public const string NotConnectedCode = "not_connected";

    private volatile IWireRequester? _inner;

    /// <summary>Raised after <see cref="Set"/>, on the caller's thread, so a pane can refresh. Marshal to the UI thread in the handler.</summary>
    public event EventHandler? Attached;

    /// <summary>True once a requester has been set.</summary>
    public bool IsAttached => _inner is not null;

    /// <summary>Sets the requester that later requests are forwarded to, and raises <see cref="Attached"/>.</summary>
    public void Set(IWireRequester requester)
    {
        ArgumentNullException.ThrowIfNull(requester);
        _inner = requester;
        Attached?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
    {
        var inner = _inner;
        return inner is null
            ? Task.FromException<TResponse>(new WireRequestException(new WireError(NotConnectedCode, "No server connection yet.", null)))
            : inner.RequestAsync<TRequest, TResponse>(type, payload, cancellationToken);
    }
}
