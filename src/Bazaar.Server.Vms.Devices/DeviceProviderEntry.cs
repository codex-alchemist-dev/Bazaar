using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Vms.Devices;

/// <summary>
/// The shared catalogue entry and backend source of an on-demand provider module (doc 62 sections 9.1 and 9.2). It builds the
/// provider's backend from its settings and keeps a short cache of them:
/// <list type="bullet">
/// <item>While no backend exists (a required value is unset or invalid), every refresh reads the install values again, so finishing
/// the configuration takes effect without a restart.</item>
/// <item>Once a backend exists it is never rebuilt. Install values then apply after a restart only, so a running guest keeps the
/// SDK, image or state folder it started from. Live values (<see cref="DeviceProvidersSettings.LiveKeys"/>) are applied to the same
/// backend object on each refresh, so they affect only new starts and never a running guest.</item>
/// </list>
/// <para>
/// A change to an install value after the backend was built cannot apply, because nothing is rebuilt. On each refresh the entry
/// compares the values the backend was built from with a fresh read, lists the changed keys in <see cref="RestartRequiredKeys"/> and
/// logs one warning per changed key. The warning names the key and never the value, since paths and images may be sensitive. There is
/// no change notification, so a change is seen at the next refresh.
/// </para>
/// Reads are async (<see cref="RefreshAsync"/>, <see cref="GetCurrentAsync"/>). The synchronous members report the last loaded state
/// and never block on the settings store; before the first refresh they report not usable.
/// </summary>
public abstract class DeviceProviderEntry<TBackend> : IDeviceProviderCatalogEntry, IVmBackendSource, IVmProvider, IDisposable
    where TBackend : class, IVmBackend, IVmProvider, IDeviceProviderCatalogEntry, IDisposable
{
    /// <summary>How long a read of the settings is reused before the next refresh reads them again.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(5);

    private const string _notLoaded = "The provider settings have not been read yet.";

    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot _snapshot = new(null, _notLoaded, null);
    private readonly ILogger? _logger;
    private int _invalidated;
    private long _generation;
    private IReadOnlyDictionary<string, string>? _builtFrom;
    private string[] _restartRequired = [];

    protected DeviceProviderEntry(TimeProvider? clock, TimeSpan? refreshInterval, ILogger? logger = null)
    {
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _interval = refreshInterval ?? DefaultRefreshInterval;
        ArgumentOutOfRangeException.ThrowIfLessThan(_interval, TimeSpan.Zero);
    }

    public abstract string Kind { get; }

    public abstract IReadOnlyList<VmOs> GuestOs { get; }

    public VmProviderPlacement Placement => VmProviderPlacement.OnDemand;

    public virtual int Preference => VmBackendOrdering.OnDemand;

    /// <summary>The built backend, or null while a required value is unset or invalid, or before the first refresh.</summary>
    public TBackend? Backend => Volatile.Read(ref _snapshot).Backend;

    /// <summary>Bumps each time a refresh builds the backend, changes the reason, or applies a changed live value.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Install setting keys that changed after the backend was built, in ordinal order. Empty when none changed, or while no backend
    /// exists (the next refresh reads the new values anyway). A key set back to the built value leaves the list.
    /// </summary>
    public IReadOnlyList<string> RestartRequiredKeys => Volatile.Read(ref _restartRequired);

    public DeviceProviderCapabilities Capabilities => Backend?.Capabilities ?? DefaultCapabilities;

    public int Running => Backend?.Running ?? 0;

    /// <summary>The report when no backend is built.</summary>
    protected abstract DeviceProviderCapabilities DefaultCapabilities { get; }

    /// <summary>The plain reason shown when the settings store cannot be read. It names no path.</summary>
    protected abstract string ReadFailureReason { get; }

    /// <summary>Makes the next refresh read the settings at once, whatever the interval.</summary>
    public void Invalidate() => Volatile.Write(ref _invalidated, 1);

    public DeviceProbeResult ProbeDevice()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Backend?.ProbeDevice() ?? new DeviceProbeResult(false, snapshot.Reason);
    }

    public ProviderStatus Probe()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Backend?.Probe() ?? new ProviderStatus(Kind, false, [], snapshot.Reason);
    }

    public IReadOnlyList<DeviceGuestState> RunningGuests() => Backend?.RunningGuests() ?? [];

    public async ValueTask RefreshAsync(CancellationToken ct)
    {
        if (!IsDue())
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsDue())
                return;
            Volatile.Write(ref _invalidated, 0);
            var current = Volatile.Read(ref _snapshot);
            try
            {
                if (current.Backend is null)
                {
                    _builtFrom = null;
                    var (backend, reason) = await BuildAsync(ct).ConfigureAwait(false);
                    Volatile.Write(ref _snapshot, new Snapshot(backend, backend is null ? reason : null, _clock.GetUtcNow()));
                    if (backend is not null || reason != current.Reason)
                        Interlocked.Increment(ref _generation);
                }
                else
                {
                    var changed = await ApplyLiveAsync(current.Backend, ct).ConfigureAwait(false);
                    changed |= await DetectRestartRequiredAsync(ct).ConfigureAwait(false);
                    Volatile.Write(ref _snapshot, current with { FreshAt = _clock.GetUtcNow() });
                    if (changed)
                        Interlocked.Increment(ref _generation);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not cached as fresh: a store that is down now may answer on the next refresh. A built backend stays as it was.
                Volatile.Write(ref _snapshot, current.Backend is null ? new Snapshot(null, ReadFailureReason, null) : current);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask<VmBackendLookup> GetCurrentAsync(CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Backend is not { } backend)
            return VmBackendLookup.Unavailable(snapshot.Reason ?? _notLoaded);
        var status = backend.Probe();
        return new VmBackendLookup(backend, status.Usable, status.Usable ? null : status.Reason);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        Backend?.Dispose();
        _refreshGate.Dispose();
    }

    /// <summary>
    /// Reads the install values and builds the backend, or returns the plain reason it cannot (never a path). Called only while no
    /// backend exists. A settings store failure may throw; it is reported as <see cref="ReadFailureReason"/>.
    /// </summary>
    protected abstract Task<(TBackend? Backend, string? Reason)> BuildAsync(CancellationToken ct);

    /// <summary>Reads the live values and applies them to the built backend. Returns true when a value changed.</summary>
    protected abstract Task<bool> ApplyLiveAsync(TBackend backend, CancellationToken ct);

    /// <summary>
    /// Reads the current text of each install value this provider builds its backend from, keyed by setting name. The text is only
    /// compared, never logged or sent. May throw when the store is down; the refresh then keeps the last result. Empty by default.
    /// </summary>
    protected virtual Task<IReadOnlyDictionary<string, string>> ReadInstallValuesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    /// <summary>
    /// Called by <see cref="BuildAsync"/> with the install values it built the backend from, just before it returns the backend, so
    /// later reads have something to be compared with. Not calling it turns detection off for this provider.
    /// </summary>
    protected void RecordBuiltFrom(IReadOnlyDictionary<string, string> values) => _builtFrom = values;

    private async Task<bool> DetectRestartRequiredAsync(CancellationToken ct)
    {
        if (_builtFrom is not { } built)
            return false;

        var now = await ReadInstallValuesAsync(ct).ConfigureAwait(false);
        var changedKeys = built
            .Where(kv => !now.TryGetValue(kv.Key, out var value) || !string.Equals(value, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var before = Volatile.Read(ref _restartRequired);
        if (changedKeys.AsSpan().SequenceEqual(before))
            return false;

        // One warning per key that newly needs a restart. A key set back to its built value leaves the list, so a later change warns again.
        if (_logger is not null)
        {
            foreach (var key in changedKeys.Except(before, StringComparer.Ordinal))
                DeviceProviderLog.RestartRequired(_logger, key, Kind);
        }

        Volatile.Write(ref _restartRequired, changedKeys);
        return true;
    }

    private bool IsDue()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.FreshAt is not { } at || Volatile.Read(ref _invalidated) == 1 || _clock.GetUtcNow() - at >= _interval;
    }

    private sealed record Snapshot(TBackend? Backend, string? Reason, DateTimeOffset? FreshAt);
}

internal static partial class DeviceProviderLog
{
    // The key only: setting values (paths, images) are never logged.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting {Key} changed after the {Kind} device provider was built; a server restart is needed for it to apply.")]
    internal static partial void RestartRequired(ILogger logger, string key, string kind);
}
