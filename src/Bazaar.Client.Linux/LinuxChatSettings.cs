using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Linux;

/// <summary>
/// Decides what the head's chat connects to (doc 30). Order:
/// <list type="number">
/// <item>The environment (<c>BAZAAR_SERVER</c>, <c>BAZAAR_GC</c>, <c>BAZAAR_TOKEN</c>) is the dev override and always wins.</item>
/// <item>Otherwise the server is the one this build was made for (<see cref="ClientServer.Current"/>), the token is the saved
/// session's, and the group chat is <c>--dev-gc &lt;id&gt;</c> or the one opened last time (<c>last-gc.json</c>).</item>
/// </list>
/// No saved session, no known group chat, or an endpoint that refuses contact means null: the window opens on the Dashboard (or the
/// sign-in screen). The server has no "list my group chats" request on main yet, so the group chat cannot be chosen from the server.
/// A remembered group chat is only used on the server it was remembered for, so a saved token is never sent to another server.
/// </summary>
internal static class LinuxChatSettings
{
    public const string DevGcFlag = "--dev-gc";

    /// <exception cref="ArgumentException">The environment or <c>--dev-gc</c> holds an invalid value.</exception>
    public static ChatConnectionSettings? Resolve(
        IReadOnlyList<string> args,
        Func<string, string?> env,
        LinuxPaths paths,
        ISessionStore store,
        Func<ServerEndpoint?> endpoint,
        TimeProvider time,
        Func<ISessionStore, Task<bool>> refresh)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);

        var devGc = DevGc(args);
        var fromEnv = ChatConnectionSettings.FromEnvironment(LinuxClientSessions.EnvWithStoredToken(env, () => store, time, refresh));
        if (fromEnv is not null)
        {
            Remember(paths, fromEnv);
            return fromEnv;
        }

        var current = endpoint();
        if (current is null) return null;
        if (current.RefusalReason() is not null) return null;
        var server = EndpointWireClient.SocketUri(current);

        var gc = devGc;
        if (gc is null && LastGcStore.Load(paths.LastGcFile) is { } last && string.Equals(last.Server, server.AbsoluteUri, StringComparison.Ordinal))
        {
            gc = last.GcId;
        }

        if (gc is null) return null;

        // Fail closed: no token, no connection.
        var token = env(ChatConnectionSettings.TokenVariable) is { } t && !string.IsNullOrWhiteSpace(t)
            ? t.Trim()
            : LinuxClientSessions.LoadStoredToken(store, time, refresh);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var name = env(ChatConnectionSettings.NameVariable) is { } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "you";
        var settings = new ChatConnectionSettings(server, token, gc.Value, name);
        Remember(paths, settings);
        return settings;
    }

    private static void Remember(LinuxPaths paths, ChatConnectionSettings settings) =>
        LastGcStore.Save(paths.LastGcFile, new LastGcStore.Entry(settings.GcId, settings.Server.AbsoluteUri));

    private static Guid? DevGc(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != DevGcFlag) continue;
            if (i + 1 >= args.Count || !Guid.TryParse(args[i + 1], out var id))
                throw new ArgumentException($"{DevGcFlag} needs a group chat id (a GUID).", nameof(args));
            return id;
        }

        return null;
    }
}
