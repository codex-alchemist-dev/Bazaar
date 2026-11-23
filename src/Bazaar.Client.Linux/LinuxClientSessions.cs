using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Chat;
using Bazaar.Client.Linux.Secrets;
using Bazaar.Client.SignIn;
using Bazaar.Contracts;

namespace Bazaar.Client.Linux;

/// <summary>
/// The one session store of this process, shared by the sign-in screen and the start-up connection (doc 45 section 2.6,
/// doc 30). The session is sealed with AES-GCM; the key lives in the Secret Service keyring when <c>secret-tool</c> works and in
/// a separate 0600 file otherwise (doc 14 key custody default <c>file</c>). When neither can hold a key the session stays in memory.
/// </summary>
internal static class LinuxClientSessions
{
    private static readonly Lazy<Selection> _default = new(() => Select(
        new ProcessSecretToolRunner(),
        LinuxPaths.FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        Console.Error.WriteLine));

    /// <summary>The session store of this process.</summary>
    public static ISessionStore Store => Override ?? _default.Value.Store;

    /// <summary>Tests replace the process store so no run touches the real keyring or state directory.</summary>
    internal static ISessionStore? Override { get; set; }

    /// <summary>Builds the store and says which key custody it uses. Never logs a secret.</summary>
    internal static Selection Select(ISecretToolRunner runner, LinuxPaths paths, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(report);

        var protector = LinuxSecretProtector.TryOpen(runner, paths.StateDirectory);
        if (protector is null)
        {
            report("Session store: no keyring and no writable key file; the sign-in lasts until this program closes.");
            return new Selection(new InMemorySessionStore(), null);
        }

        report(protector.Custody == SessionKeyCustody.Libsecret
            ? "Session store: key held in the desktop keyring (libsecret)."
            : "Session store: no keyring found; key held in a private file (mode 0600) beside the session.");
        return new Selection(new ProtectedFileSessionStore(Path.Combine(paths.StateDirectory, "session.bin"), protector), protector.Custody);
    }

    /// <summary>
    /// Adds <c>--sign-in</c> when the head has nothing to connect to (no settings), no session is saved and no first-run screen was
    /// asked for. A warm preload start never opens a sign-in.
    /// </summary>
    public static IReadOnlyList<string> WithAutoSignIn(IReadOnlyList<string> args, bool noSettings, bool preload) =>
        WithAutoSignIn(args, noSettings, preload, Store);

    internal static IReadOnlyList<string> WithAutoSignIn(IReadOnlyList<string> args, bool noSettings, bool preload, ISessionStore store)
    {
        if (!noSettings || preload) return args;
        var (signIn, company) = FirstRunScreens.Parse(args);
        if (signIn || company) return args;
        if (store.LoadAsync().GetAwaiter().GetResult() is not null) return args;
        return [.. args, FirstRunScreens.SignInSwitch];
    }

    /// <summary>
    /// Wraps the environment so a missing <c>BAZAAR_TOKEN</c> is filled from the saved session (refreshed first when it has expired),
    /// letting the head connect without a prompt. A token in the environment always wins.
    /// </summary>
    public static Func<string, string?> EnvWithStoredToken(Func<string, string?> env) =>
        EnvWithStoredToken(env, () => Store, TimeProvider.System, RefreshAsync);

    internal static Func<string, string?> EnvWithStoredToken(
        Func<string, string?> env,
        Func<ISessionStore> store,
        TimeProvider time,
        Func<ISessionStore, Task<bool>> refresh) =>
        name =>
        {
            var value = env(name);
            if (name != ChatConnectionSettings.TokenVariable || !string.IsNullOrWhiteSpace(value)) return value;
            if (string.IsNullOrWhiteSpace(env(ChatConnectionSettings.ServerVariable)) || string.IsNullOrWhiteSpace(env(ChatConnectionSettings.GcVariable))) return value;

            return LoadStoredToken(store(), time, refresh) ?? value;
        };

    /// <summary>The saved access token, refreshed first when it has expired. Null when there is no session or the refresh failed.</summary>
    internal static string? LoadStoredToken(ISessionStore sessions, TimeProvider time, Func<ISessionStore, Task<bool>> refresh) =>
        Task.Run(async () =>
        {
            var session = await sessions.LoadAsync().ConfigureAwait(false);
            if (session is null) return null;
            if (session.AccessTokenExpiresAt > time.GetUtcNow().AddSeconds(30)) return session.AccessToken;
            return await refresh(sessions).ConfigureAwait(false) ? (await sessions.LoadAsync().ConfigureAwait(false))?.AccessToken : null;
        }).GetAwaiter().GetResult();

    /// <summary>Refreshes the saved session against the current server. False when it could not be refreshed.</summary>
    internal static Task<bool> RefreshStore(ISessionStore store) => RefreshAsync(store);

    private static async Task<bool> RefreshAsync(ISessionStore store)
    {
        try
        {
            using var http = ClientServer.Current.CreateHttpClient();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return (await new HttpSignInService(http, store, "linux", ProductVersion.Informational).RefreshAsync(budget.Token).ConfigureAwait(false)).Succeeded;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The chosen store and where its key lives (null for memory only).</summary>
    internal sealed record Selection(ISessionStore Store, SessionKeyCustody? Custody);
}
