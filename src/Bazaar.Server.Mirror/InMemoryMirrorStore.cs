namespace Bazaar.Server.Mirror;

/// <summary>A process-local store, for tests and a first run. The Postgres store replaces it and encrypts payloads.</summary>
public sealed class InMemoryMirrorStore : IMirrorStore
{
    private readonly Lock _gate = new();
    private readonly List<MirrorSignal> _signals = [];
    private readonly List<MirrorProfile> _profiles = [];
    private readonly HashSet<string> _suppressed = [];
    private bool _paused;

    public Task AddSignalAsync(MirrorSignal signal, CancellationToken cancellationToken)
    {
        lock (_gate) { _signals.Add(signal); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MirrorSignal>> ListSignalsAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { return Task.FromResult<IReadOnlyList<MirrorSignal>>([.. _signals]); }
    }

    public Task AddProfileAsync(MirrorProfile profile, CancellationToken cancellationToken)
    {
        lock (_gate) { _profiles.Add(profile); }
        return Task.CompletedTask;
    }

    public Task<MirrorProfile?> GetLatestProfileAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { return Task.FromResult(_profiles.Count == 0 ? null : _profiles[^1]); }
    }

    public Task<bool> GetPausedAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { return Task.FromResult(_paused); }
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        lock (_gate) { _paused = paused; }
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> ListSuppressedAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(_suppressed)); }
    }

    public Task AddSuppressedAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate) { _suppressed.Add(key); }
        return Task.CompletedTask;
    }

    public Task WipeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _signals.Clear();
            _profiles.Clear();
            _suppressed.Clear();
        }

        return Task.CompletedTask;
    }
}
