using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>
/// The one session store of this process, shared by the sign-in screen and the merge identity. It is the DPAPI-sealed file store when Windows allows
/// it, and memory only when it does not.
/// </summary>
internal static class ClientSessions
{
    public static ISessionStore Store { get; } = FirstRunScreens.SessionStores.Create();
}
