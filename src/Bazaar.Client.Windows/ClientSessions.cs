using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>
/// The one session store of this process, shared by the sign-in screen and the merge identity. It is the DPAPI-sealed file store when Windows
/// allows it (memory only otherwise), so a restart stays signed in.
/// </summary>
internal static class ClientSessions
{
    public static ISessionStore Store { get; } = FirstRunScreens.SessionStores.Create();
}
