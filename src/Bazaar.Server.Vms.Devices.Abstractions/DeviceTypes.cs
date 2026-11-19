using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.Devices;

/// <summary>How strongly a device guest is isolated from its host (doc 62 section 4).</summary>
public enum DeviceIsolation
{
    /// <summary>A hypervisor-backed virtual machine.</summary>
    HardwareVirtualised,

    /// <summary>A process on the host (the iOS Simulator). Reported plainly, never as a VM.</summary>
    HostProcess,
}

/// <summary>What a device provider supports. Every value is backed by tested behaviour (doc 62 section 4).</summary>
public sealed record DeviceProviderCapabilities(
    string GuestOs,
    DeviceIsolation Isolation,
    bool Save,
    bool Checkpoint,
    bool Restore,
    bool Headless,
    int MaxConcurrentInstances,
    bool GuestAgent,
    bool EgressEnforced,
    bool ParentContentPinned,
    bool ScreenSource,
    bool RemoteNode);

/// <summary>A PNG frame from a device screen. Untrusted external content (doc 59 rule 1).</summary>
public sealed record DeviceScreen(byte[] Png, DateTimeOffset CapturedAt);

/// <summary>A tap, key or text input for a device. Built host-side, never a shell string.</summary>
public abstract record DeviceInput
{
    public sealed record Tap(int X, int Y) : DeviceInput;

    public sealed record Key(int KeyCode) : DeviceInput;

    public sealed record Text(string Value) : DeviceInput;
}

/// <summary>What a device provider offers beyond the VM lifecycle (doc 62 section 4). Typed calls only.</summary>
public interface IDeviceControl
{
    Task InstallAppAsync(Guid vmId, string packagePathInWorkspace, CancellationToken ct);

    Task LaunchAppAsync(Guid vmId, string appId, CancellationToken ct);

    Task<DeviceScreen> CaptureAsync(Guid vmId, CancellationToken ct);

    Task SendInputAsync(Guid vmId, DeviceInput input, CancellationToken ct);
}

/// <summary>Validators for values that reach a device tool's argv. A rejected value throws; nothing is escaped or repaired.</summary>
public static partial class DeviceIdentifiers
{
    /// <summary>Dotted app package id: at least two segments, each starting with a letter.</summary>
    public static string AppId(string value) => Check(value, AppIdRegex(), 255, "app id");

    /// <summary>Lowercase AVD or device name, letters, digits and underscores.</summary>
    public static string Name(string value) => Check(value, NameRegex(), 64, "device name");

    /// <summary>A relative path inside the workspace share: no root, no dot segments, no backslash, no NUL.</summary>
    public static string WorkspaceRelativePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 1024 || value.StartsWith('/') || value.Contains('\\') || value.Contains('\0')
            || value.Split('/').Any(s => s is "" or "." or ".."))
        {
            throw new ArgumentException("Path must be a relative path inside the workspace.", nameof(value));
        }

        return value;
    }

    /// <summary>Text typed into a device. Printable characters only, limited length, so no control byte reaches the tool.</summary>
    public static string InputText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 512 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Input text must be 1 to 512 printable characters.", nameof(value));
        }

        return value;
    }

    private static string Check(string value, Regex regex, int max, string what)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > max || !regex.IsMatch(value))
        {
            throw new ArgumentException($"Invalid {what}.", nameof(value));
        }

        return value;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
