using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.Ios;

/// <summary>
/// The simulator device a guest is created with (doc 62 section 5.2): a device type and the runtime it runs on. Both are Apple
/// CoreSimulator identifiers, validated before they reach the Mac node.
/// <para>Apple rule: the iOS Simulator is licensed for Apple hardware only, so this is used on a Mac node and nowhere else.</para>
/// </summary>
/// <param name="DeviceType">For example <c>com.apple.CoreSimulator.SimDeviceType.iPhone-16</c>.</param>
/// <param name="Runtime">For example <c>com.apple.CoreSimulator.SimRuntime.iOS-18-0</c>. The pinned parent.</param>
public sealed record IosDeviceProfile(string DeviceType, string Runtime)
{
    /// <summary>The device type used when none is configured.</summary>
    public const string DefaultDeviceType = "com.apple.CoreSimulator.SimDeviceType.iPhone-16";

    /// <summary>Throws <see cref="ArgumentException"/> when either identifier is not a CoreSimulator id.</summary>
    public IosDeviceProfile Validated()
    {
        _ = IosIdentifiers.DeviceType(DeviceType);
        _ = IosIdentifiers.Runtime(Runtime);
        return this;
    }
}

/// <summary>Validators for the identifiers that reach <c>simctl</c> through the Mac node. A rejected value throws; nothing is repaired.</summary>
public static partial class IosIdentifiers
{
    /// <summary>A simulator device type id, <c>com.apple.CoreSimulator.SimDeviceType.&lt;name&gt;</c>.</summary>
    public static string DeviceType(string value) => Check(value, DeviceTypeRegex(), "simulator device type");

    /// <summary>A simulator runtime id, <c>com.apple.CoreSimulator.SimRuntime.&lt;name&gt;</c>.</summary>
    public static string Runtime(string value) => Check(value, RuntimeRegex(), "simulator runtime");

    private static string Check(string value, Regex regex, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 200 || !regex.IsMatch(value))
        {
            throw new ArgumentException($"Invalid {what} id.", nameof(value));
        }

        return value;
    }

    [GeneratedRegex(@"^com\.apple\.CoreSimulator\.SimDeviceType\.[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceTypeRegex();

    [GeneratedRegex(@"^com\.apple\.CoreSimulator\.SimRuntime\.[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeRegex();
}
