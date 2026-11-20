namespace Bazaar.Server.Vms.Android;

/// <summary>
/// Settings of the Android provider (doc 62 section 7). SDK and AVD folders are required and explicit: the provider never falls back to a
/// profile default, so nothing is written to a folder the operator did not choose.
/// </summary>
public sealed record AndroidOptions
{
    /// <summary><c>vms.android.sdk_root</c>.</summary>
    public required string SdkRoot { get; init; }

    /// <summary><c>vms.android.avd_home</c>.</summary>
    public required string AvdHome { get; init; }

    /// <summary><c>vms.android.system_image</c>: the SDK package id, for example <c>system-images;android-35;google_apis;x86_64</c>.</summary>
    public required string SystemImagePackage { get; init; }

    /// <summary>The golden image version id that stands for the pinned system image (parent content pinned, doc 62).</summary>
    public required Guid SystemImageVersion { get; init; }

    /// <summary><c>vms.android.max_concurrent</c>.</summary>
    public int MaxConcurrent { get; init; } = AndroidModule.Capabilities.MaxConcurrentInstances;

    /// <summary>First emulator console port; each VM takes the next even port. Console and adb ports are loopback only.</summary>
    public int BasePort { get; init; } = 5554;

    /// <summary>Throws when a required path is empty or relative, or a limit is out of range.</summary>
    public void Validate()
    {
        foreach (var (name, value) in new[] { ("SdkRoot", SdkRoot), ("AvdHome", AvdHome) })
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
            {
                throw new InvalidOperationException($"vms.android {name} must be an explicit absolute path.");
            }
        }

        if (string.IsNullOrWhiteSpace(SystemImagePackage) || SystemImagePackage.Any(c => c is not (';' or '-' or '_' or '.') && !char.IsAsciiLetterOrDigit(c)))
        {
            throw new InvalidOperationException("vms.android.system_image must be an SDK package id.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BasePort, 5554);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BasePort, 5584);
    }
}
