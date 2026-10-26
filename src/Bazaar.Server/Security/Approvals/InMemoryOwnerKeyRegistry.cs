namespace Bazaar.Server.Security.Approvals;

/// <summary>Process-local owner key registry. Keys are added by the owner's setup flow; revocation flips <see cref="OwnerDeviceKey.Revoked"/>.</summary>
public sealed class InMemoryOwnerKeyRegistry : IOwnerKeyRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OwnerDeviceKey> _keys = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces the key for a device.</summary>
    public void Register(OwnerDeviceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!ApprovalSignature.IsValidDeviceId(key.DeviceId))
        {
            throw new ArgumentException("Device id has a disallowed shape.", nameof(key));
        }

        lock (_gate)
        {
            _keys[key.DeviceId] = key;
        }
    }

    /// <inheritdoc />
    public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_keys.GetValueOrDefault(deviceId));
        }
    }
}
