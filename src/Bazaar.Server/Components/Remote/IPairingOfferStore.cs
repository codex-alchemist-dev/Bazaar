namespace Bazaar.Server.Components.Remote;

/// <summary>One pairing offer as stored: hashes only, never the plain user code or device code.</summary>
/// <param name="State">One of pending, approved, redeemed, expired, burned or denied (the table's CHECK).</param>
public sealed record PairingOfferRecord(
    Guid Id,
    Guid CreatedBy,
    string Role,
    byte[] UserCodeHash,
    byte[]? DeviceCodeHash,
    DateTimeOffset ExpiresAt,
    string State,
    string? DeviceLabel,
    string? DeviceClass,
    DateTimeOffset? LastPolledAt);

/// <summary>
/// Storage for pairing offers. Each state change is one compare-and-set, so an approval, a redemption and an expiry cannot both win.
/// </summary>
public interface IPairingOfferStore
{
    Task InsertAsync(PairingOfferRecord offer, CancellationToken ct = default);

    Task<PairingOfferRecord?> FindByUserCodeHashAsync(byte[] hash, CancellationToken ct = default);

    Task<PairingOfferRecord?> FindByDeviceCodeHashAsync(byte[] hash, CancellationToken ct = default);

    Task<PairingOfferRecord?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>Attaches a device request to a pending offer that has none. Returns false when the offer already has one or is not pending.</summary>
    Task<bool> TryAttachDeviceAsync(Guid id, byte[] deviceCodeHash, string label, string deviceClass, CancellationToken ct = default);

    /// <summary>Moves the offer from <paramref name="fromState"/> to <paramref name="toState"/>. Returns false when it is not in <paramref name="fromState"/>.</summary>
    Task<bool> TryTransitionAsync(Guid id, string fromState, string toState, CancellationToken ct = default);

    /// <summary>Records the time of the device's latest poll, for slow_down.</summary>
    Task TouchPollAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>An in-process <see cref="IPairingOfferStore"/> with the same compare-and-set rules as the Postgres store. Tests and database-less hosts.</summary>
public sealed class InMemoryPairingOfferStore : IPairingOfferStore
{
    private readonly object _gate = new();
    private readonly List<PairingOfferRecord> _offers = [];

    public Task InsertAsync(PairingOfferRecord offer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        lock (_gate)
        {
            if (_offers.Any(o => o.Id == offer.Id || o.UserCodeHash.AsSpan().SequenceEqual(offer.UserCodeHash)))
                throw new InvalidOperationException("A pairing offer with this id or code already exists.");
            _offers.Add(offer);
        }
        return Task.CompletedTask;
    }

    public Task<PairingOfferRecord?> FindByUserCodeHashAsync(byte[] hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate) return Task.FromResult(_offers.FirstOrDefault(o => o.UserCodeHash.AsSpan().SequenceEqual(hash)));
    }

    public Task<PairingOfferRecord?> FindByDeviceCodeHashAsync(byte[] hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate)
            return Task.FromResult(_offers.FirstOrDefault(o => o.DeviceCodeHash is { } h && h.AsSpan().SequenceEqual(hash)));
    }

    public Task<PairingOfferRecord?> FindAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_offers.FirstOrDefault(o => o.Id == id));
    }

    public Task<bool> TryAttachDeviceAsync(Guid id, byte[] deviceCodeHash, string label, string deviceClass, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _offers.FindIndex(o => o.Id == id);
            if (index < 0 || _offers[index].State != "pending" || _offers[index].DeviceCodeHash is not null)
                return Task.FromResult(false);
            _offers[index] = _offers[index] with { DeviceCodeHash = deviceCodeHash, DeviceLabel = label, DeviceClass = deviceClass };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryTransitionAsync(Guid id, string fromState, string toState, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _offers.FindIndex(o => o.Id == id);
            if (index < 0 || _offers[index].State != fromState) return Task.FromResult(false);
            _offers[index] = _offers[index] with { State = toState };
            return Task.FromResult(true);
        }
    }

    public Task TouchPollAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _offers.FindIndex(o => o.Id == id);
            if (index >= 0) _offers[index] = _offers[index] with { LastPolledAt = at };
        }
        return Task.CompletedTask;
    }
}
