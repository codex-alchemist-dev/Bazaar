using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Ios;

/// <summary>
/// Settings of the iOS Simulator provider (doc 62 section 7). The runtime pin is the <c>vms.ios.runtime_pin</c> setting; the device
/// type is the shipped default.
/// </summary>
public sealed record IosOptions
{
    /// <summary><c>vms.ios.runtime_pin</c>: the pinned simulator runtime id (the parent). Empty means not pinned, and the provider reports not usable.</summary>
    public string RuntimeId { get; init; } = string.Empty;

    /// <summary>The runtime build that was pinned with <see cref="RuntimeId"/>. Informational; part of the derived image version.</summary>
    public string RuntimeBuild { get; init; } = string.Empty;

    /// <summary>The golden image version id that stands for the pinned runtime. A child is accepted only from this parent.</summary>
    public required Guid RuntimeVersion { get; init; }

    /// <summary>The simulator device type every child is created with.</summary>
    public string DeviceType { get; init; } = IosDeviceProfile.DefaultDeviceType;

    /// <summary><c>vms.ios.max_concurrent</c>.</summary>
    public int MaxConcurrent { get; init; } = IosModule.Capabilities.MaxConcurrentInstances;

    /// <summary>Throws when an identifier is not a CoreSimulator id or the limit is out of range.</summary>
    public void Validate()
    {
        _ = IosIdentifiers.DeviceType(DeviceType);
        if (RuntimeId.Length > 0)
        {
            _ = IosIdentifiers.Runtime(RuntimeId);
        }

        if (RuntimeBuild.Length > 32 || RuntimeBuild.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException("The pinned runtime build must be at most 32 letters and digits.", nameof(RuntimeBuild));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrent, IosBackend.MaxSlots);
    }
}
