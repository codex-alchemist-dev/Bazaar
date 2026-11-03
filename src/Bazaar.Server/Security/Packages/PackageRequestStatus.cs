namespace Bazaar.Server.Security.Packages;

/// <summary>Lifecycle of one package request as the owner and the install step see it.</summary>
public enum PackageRequestStatus
{
    /// <summary>The agent asked. The policy has not finished yet.</summary>
    Requested,

    /// <summary>The owner's approval card is waiting.</summary>
    AwaitingOwner,

    /// <summary>The owner approved the exact request.</summary>
    Approved,

    /// <summary>Refused, by the policy or by the owner.</summary>
    Denied,

    /// <summary>No decision arrived before expiry.</summary>
    Expired,

    /// <summary>The package was installed.</summary>
    Installed,

    /// <summary>The install failed.</summary>
    Failed,
}

/// <summary>Receives package request status changes. Implementations must not block; the publisher calls them inline.</summary>
public interface IPackageRequestStatusListener
{
    /// <summary>Called once for each status change of a request.</summary>
    void OnStatusChanged(Guid requestId, PackageRequestStatus status);
}

/// <summary>Publishes package request status changes to the subscribed listeners.</summary>
public sealed class PackageRequestStatusPublisher(Action<string, Exception> onListenerError)
{
    private readonly StatusListenerPublisher<IPackageRequestStatusListener> _listeners = new(onListenerError);

    /// <summary>Adds a listener.</summary>
    public void Subscribe(IPackageRequestStatusListener listener) => _listeners.Subscribe(listener);

    /// <summary>Tells every listener about one status change, in order.</summary>
    public void Publish(Guid requestId, PackageRequestStatus status) =>
        _listeners.Publish(listener => listener.OnStatusChanged(requestId, status));
}
