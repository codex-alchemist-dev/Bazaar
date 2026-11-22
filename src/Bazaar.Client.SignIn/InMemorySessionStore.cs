using Bazaar.Client.SignIn;

namespace Bazaar.Client.SignIn;

/// <summary>
/// Keeps the session for this run only. It is the default until a head supplies a protected store, so a restart signs out
/// rather than writing a token to disk in the clear.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private ClientSession? _session;

    /// <inheritdoc />
    public Task<ClientSession?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _session));

    /// <inheritdoc />
    public Task SaveAsync(ClientSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Volatile.Write(ref _session, session);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _session, null);
        return Task.CompletedTask;
    }
}
