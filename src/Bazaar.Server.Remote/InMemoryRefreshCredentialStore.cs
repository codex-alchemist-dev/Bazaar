using System.Security.Cryptography;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IRefreshCredentialStore"/> held in process memory. Used by unit tests and by any host that runs without a database.
/// It follows the same rules as <see cref="PostgresRefreshCredentialStore"/>: one live credential per device, atomic rotation,
/// and revocation that is set once.
/// </summary>
public sealed class InMemoryRefreshCredentialStore : IRefreshCredentialStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DeviceRecord> _devices = [];
    private readonly List<CredentialRow> _credentials = [];

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
