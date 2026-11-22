using System.Security.Cryptography;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IRefreshCredentialStore"/> held in process memory. Used by unit tests and by any host that runs without a database.
/// It follows the same rules as <see cref="PostgresRefreshCredentialStore"/>: one live credential per device, atomic rotation,
/// and revocation that is set once.
/// </summary>
public sealed class InMemoryRefreshCredentialStore : IRefreshCredentialStore, IIdentityDirectoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DeviceRecord> _devices = [];
    private readonly Dictionary<Guid, HumanRecord> _humans = [];
    private readonly List<CredentialRow> _credentials = [];

    /// <summary>Adds or replaces a human row. The in-memory store has no pairing flow that creates humans, so tests and dev hosts seed them here.</summary>
    public void SetHuman(HumanRecord human)
    {
        ArgumentNullException.ThrowIfNull(human);
        lock (_gate)
        {
            _humans[human.Id] = human;
        }
    }

    public Task<IReadOnlyList<HumanRecord>> ListHumansAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<HumanRecord> rows = [.. _humans.Values.OrderBy(h => h.DisplayName, StringComparer.Ordinal).ThenBy(h => h.Id)];
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<DeviceDirectoryEntry>> ListDevicesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<DeviceDirectoryEntry> rows =
            [
                .. _devices.Values
                    .OrderBy(d => d.Label, StringComparer.Ordinal).ThenBy(d => d.Id)
                    .Select(d => new DeviceDirectoryEntry(
                        d,
                        _credentials.Where(c => c.DeviceId == d.Id).Select(c => c.IssuedAt).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max())),
            ];
            return Task.FromResult(rows);
        }
    }

    public Task<HumanRecord?> FindHumanAsync(Guid humanId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_humans.GetValueOrDefault(humanId));
        }
    }

    public Task<RefreshCredentialRecord?> FindByHashAsync(byte[] hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var row = _credentials.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(c.Hash, hash));
            return Task.FromResult(row is null ? null : ToRecord(row));
        }
    }

    public Task<DeviceRecord?> FindDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_devices.GetValueOrDefault(deviceId));
        }
    }

    public Task CreateDeviceAsync(
        DeviceRecord device,
        Guid credentialId,
        byte[] hash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(hash);
        ct.ThrowIfCancellationRequested();
        if (device.RevokedAt is not null)
            throw new ArgumentException("A new device cannot be revoked.", nameof(device));
        lock (_gate)
        {
            if (_devices.ContainsKey(device.Id))
                throw new InvalidOperationException("The device already exists.");
            if (_credentials.Any(c => CryptographicOperations.FixedTimeEquals(c.Hash, hash)))
                throw new InvalidOperationException("The refresh credential hash is already stored.");
            _devices[device.Id] = device;
            _credentials.Add(new CredentialRow(credentialId, device.Id, hash, issuedAt, null, expiresAt));
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryRotateAsync(
        Guid deviceId,
        Guid currentCredentialId,
        Guid nextCredentialId,
        byte[] nextHash,
        DateTimeOffset now,
        DateTimeOffset nextExpiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nextHash);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = _credentials.FirstOrDefault(c => c.Id == currentCredentialId && c.DeviceId == deviceId);
            var device = _devices.GetValueOrDefault(deviceId);
            if (current is null || current.RetiredAt is not null || device is null || device.RevokedAt is not null)
                return Task.FromResult(false);
            if (_credentials.Any(c => CryptographicOperations.FixedTimeEquals(c.Hash, nextHash)))
                throw new InvalidOperationException("The refresh credential hash is already stored.");

            current.RetiredAt = now;
            _credentials.Add(new CredentialRow(nextCredentialId, deviceId, nextHash, now, null, nextExpiresAt));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RevokeDeviceAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || device.RevokedAt is not null)
                return Task.FromResult(false);
            _devices[deviceId] = device with { RevokedAt = now, Revision = device.Revision + 1 };
            return Task.FromResult(true);
        }
    }

    public Task<SuspendResult> SuspendHumanAsync(Guid humanId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_humans.TryGetValue(humanId, out var human) || human.Status != "active" || human.Role == "owner")
                return Task.FromResult(new SuspendResult(false, []));
            _humans[humanId] = human with { Status = "suspended" };
            var revoked = new List<Guid>();
            foreach (var device in _devices.Values.Where(d => d.HumanId == humanId && d.RevokedAt is null).ToList())
            {
                _devices[device.Id] = device with { RevokedAt = now, Revision = device.Revision + 1 };
                revoked.Add(device.Id);
            }

            return Task.FromResult(new SuspendResult(true, revoked));
        }
    }

    public Task<bool> UnsuspendHumanAsync(Guid humanId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_humans.TryGetValue(humanId, out var human) || human.Status != "suspended")
                return Task.FromResult(false);
            _humans[humanId] = human with { Status = "active" };
            return Task.FromResult(true);
        }
    }

    // Callers hold _gate.
    private RefreshCredentialRecord ToRecord(CredentialRow row)
    {
        var chainStart = _credentials.Where(c => c.DeviceId == row.DeviceId).Min(c => c.IssuedAt);
        return new RefreshCredentialRecord(row.Id, row.DeviceId, row.IssuedAt, row.RetiredAt, row.ExpiresAt, chainStart);
    }

    private sealed class CredentialRow(Guid id, Guid deviceId, byte[] hash, DateTimeOffset issuedAt, DateTimeOffset? retiredAt, DateTimeOffset expiresAt)
    {
        public Guid Id { get; } = id;
        public Guid DeviceId { get; } = deviceId;
        public byte[] Hash { get; } = hash;
        public DateTimeOffset IssuedAt { get; } = issuedAt;
        public DateTimeOffset? RetiredAt { get; set; } = retiredAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}
