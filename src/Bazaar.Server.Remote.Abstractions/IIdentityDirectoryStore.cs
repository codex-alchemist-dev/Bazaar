namespace Bazaar.Server.Components.Remote;

/// <summary>A device and when it last did anything the server can see. <paramref name="LastActivity"/> is the issue time of its newest refresh credential (a device that has refreshed was in use); the <c>device</c> table keeps no other last-seen time.</summary>
public sealed record DeviceDirectoryEntry(DeviceRecord Device, DateTimeOffset LastActivity);

/// <summary>
/// Read-only listing of people and devices for the admin surfaces (doc 51 sections 4 and 5; doc 58 section 3). Secrets are not
/// part of any record: no hash, no credential id.
/// </summary>
public interface IIdentityDirectoryStore
{
    /// <summary>Every person on the instance, ordered by display name then id.</summary>
    Task<IReadOnlyList<HumanRecord>> ListHumansAsync(CancellationToken ct = default);

    /// <summary>Every device, revoked ones included, ordered by label then id.</summary>
    Task<IReadOnlyList<DeviceDirectoryEntry>> ListDevicesAsync(CancellationToken ct = default);
}
