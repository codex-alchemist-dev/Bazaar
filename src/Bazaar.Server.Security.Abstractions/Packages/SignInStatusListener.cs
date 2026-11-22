namespace Bazaar.Server.Security.Packages;

/// <summary>
/// Sign-in state of one external service account the server holds (for example a GitHub App or a package feed login).
/// </summary>
/// <remarks>TODO(component): the status vocabulary follows the Identity Service design (doc 14). Values here are provisional.</remarks>
public enum SignInStatus
{
    /// <summary>Not signed in.</summary>
    SignedOut,

    /// <summary>A sign-in is in progress.</summary>
    SigningIn,

    /// <summary>Signed in and usable.</summary>
    SignedIn,

    /// <summary>Signed in once, but the credential needs renewal or the owner's action.</summary>
    ReauthRequired,

    /// <summary>The last sign-in attempt failed.</summary>
    Failed,
}

/// <summary>Receives sign-in status changes for a service.</summary>
public interface ISignInStatusListener
{
    /// <summary>Called once for each change of a service's sign-in status.</summary>
    void OnStatusChanged(string serviceId, SignInStatus status);
}

/// <summary>Publishes sign-in status changes to the subscribed listeners. No provider is wired yet.</summary>
public sealed class SignInStatusPublisher(Action<string, Exception> onListenerError)
{
    private readonly StatusListenerPublisher<ISignInStatusListener> _listeners = new(onListenerError);

    /// <summary>Adds a listener.</summary>
    public void Subscribe(ISignInStatusListener listener) => _listeners.Subscribe(listener);

    /// <summary>Tells every listener about one status change, in order.</summary>
    public void Publish(string serviceId, SignInStatus status) =>
        _listeners.Publish(listener => listener.OnStatusChanged(serviceId, status));
}
