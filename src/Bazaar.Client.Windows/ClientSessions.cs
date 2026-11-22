using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>
/// The one session store of this process, shared by the sign-in screen and the merge identity. It is in memory until a protected
/// store (DPAPI) exists, so a restart signs out and the merge card says this client cannot approve until the person signs in again.
/// </summary>
internal static class ClientSessions
{
    public static ISessionStore Store { get; } = new InMemorySessionStore();
}
