using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices.Setup;

namespace Bazaar.Server.Vms.Devices;

/// <summary>How the microVM provider drives guests (<c>vms.microvm.monitor</c>, doc 62 section 7). Wire form is snake_case.</summary>
public enum MicroVmMonitorSetting
{
    Firecracker,
    CloudHypervisor,
}

/// <summary>
/// The device-guest provider settings of doc 62 section 7: <c>vms.devices.*</c>, <c>vms.android.*</c> and <c>vms.microvm.*</c>.
/// All are install scope and operator-changed, with safe shipped defaults (DECISIONS-LOG round 13). When a change takes effect (live,
/// or after a restart) is <see cref="LiveKeys"/>. Paths default to empty, which
/// means unset: the providers never fall back to a profile folder. The iOS, macOS and Mac node keys wait for the parked Apple work.
/// </summary>
public static class DeviceProvidersSettings
{
    /// <summary>Kinds that may be listed in <c>vms.devices.enabled</c>.</summary>
    public static readonly IReadOnlyList<string> KnownKinds = ["microvm", "android", "ios", "macos"];

    public static readonly SettingKey<IReadOnlyList<string>> Enabled = new(
        DeviceSetupValidator.EnabledKey, [], SettingScope.Install, SettingRole.Operator,
        "Device provider kinds that are on: microvm, android, ios, macos. Empty means none. Enabling one starts nothing.",
        validate: CheckKinds);

    public static readonly SettingKey<int> IdleStopMinutes = new(
        "vms.devices.idle_stop_minutes", 0, SettingScope.Install, SettingRole.Operator,
        "Minutes an unused device guest may idle before it is stopped. 0 disables idle stops. Applies without a restart.",
        validate: Between(0, 1440, "vms.devices.idle_stop_minutes"));

    public static readonly SettingKey<int> MaxConcurrent = new(
        "vms.devices.max_concurrent", 2, SettingScope.Install, SettingRole.Operator,
        "Device guests that may run at once, all providers together. 0 means no aggregate cap. The density governor may still refuse a start.",
        validate: Between(0, 64, "vms.devices.max_concurrent"));

    public static readonly SettingKey<string> AndroidSdkRoot = new(
        DeviceSetupValidator.AndroidSdkRootKey, string.Empty, SettingScope.Install, SettingRole.Operator,
        "Absolute folder of the Android SDK. Required to use the Android provider; empty means unset.",
        validate: PathOrEmpty(DeviceSetupValidator.AndroidSdkRootKey));

    public static readonly SettingKey<string> AndroidAvdHome = new(
        DeviceSetupValidator.AndroidAvdHomeKey, string.Empty, SettingScope.Install, SettingRole.Operator,
        "Absolute folder for Android virtual devices. Required to use the Android provider; empty means unset.",
        validate: PathOrEmpty(DeviceSetupValidator.AndroidAvdHomeKey));

    public static readonly SettingKey<string> AndroidSystemImage = new(
        DeviceSetupValidator.AndroidSystemImageKey, string.Empty, SettingScope.Install, SettingRole.Operator,
        "Pinned Android SDK system image package id. Required to use the Android provider; empty means unset.",
        validate: CheckSystemImage);

    public static readonly SettingKey<int> AndroidMaxConcurrent = new(
        "vms.android.max_concurrent", 2, SettingScope.Install, SettingRole.Operator,
        "Android emulators that may run at once. Applies to new starts without a restart.",
        validate: Between(1, 16, "vms.android.max_concurrent"));

    public static readonly SettingKey<MicroVmMonitorSetting> MicroVmMonitor = new(
        "vms.microvm.monitor", MicroVmMonitorSetting.Firecracker, SettingScope.Install, SettingRole.Operator,
        "The microVM monitor: firecracker or cloud_hypervisor.");

    public static readonly SettingKey<int> MicroVmMaxConcurrent = new(
        "vms.microvm.max_concurrent", 8, SettingScope.Install, SettingRole.Operator,
        "microVM guests that may run at once. Applies to new starts without a restart.",
        validate: Between(1, 256, "vms.microvm.max_concurrent"));

    public static readonly SettingKey<string> MicroVmStateDir = new(
        DeviceSetupValidator.MicroVmStateDirKey, string.Empty, SettingScope.Install, SettingRole.Operator,
        "Absolute folder for microVM disks and monitor sockets. Required to use the microVM provider; empty means unset.",
        validate: PathOrEmpty(DeviceSetupValidator.MicroVmStateDirKey));

    /// <summary>
    /// The keys doc 62 section 7 marks "live": a change reaches new starts without a restart (section 9.2). Every other key here is
    /// "install": it is read when a provider builds its backend and needs a restart once one exists.
    /// </summary>
    public static IReadOnlySet<string> LiveKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "vms.devices.idle_stop_minutes",
        "vms.devices.max_concurrent",
        "vms.android.max_concurrent",
        "vms.microvm.max_concurrent",
    };

    /// <summary>True when a change to <paramref name="key"/> applies without a restart.</summary>
    public static bool AppliesLive(SettingDefinition key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return LiveKeys.Contains(key.Name);
    }

    /// <summary>Every key this module registers.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        Enabled,
        IdleStopMinutes,
        MaxConcurrent,
        AndroidSdkRoot,
        AndroidAvdHome,
        AndroidSystemImage,
        AndroidMaxConcurrent,
        MicroVmMonitor,
        MicroVmMaxConcurrent,
        MicroVmStateDir,
    ];

    private static string? CheckKinds(IReadOnlyList<string> kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            if (!KnownKinds.Contains(kind, StringComparer.Ordinal))
            {
                return "vms.devices.enabled may list only microvm, android, ios or macos.";
            }

            if (!seen.Add(kind))
            {
                return "vms.devices.enabled lists a kind twice.";
            }
        }

        return null;
    }

    private static string? CheckSystemImage(string value) =>
        value.Length == 0 || (value.Length <= 255 && value.All(c => c is ';' or '-' or '_' or '.' || char.IsAsciiLetterOrDigit(c)))
            ? null
            : "vms.android.system_image must be an SDK package id such as system-images;android-35;google_apis;x86_64.";

    private static Func<string, string?> PathOrEmpty(string name) =>
        value => value.Length == 0 || (value.Length <= 1024 && !value.Contains('\0') && IsAbsolute(value))
            ? null
            : $"{name} must be an explicit absolute path, or empty for unset.";

    // Either spelling counts: the setting is written on one machine and may be checked on another, and a Windows drive path is not rooted on Linux.
    private static bool IsAbsolute(string value) =>
        value.StartsWith('/') || value.StartsWith(@"\\", StringComparison.Ordinal) || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');

    private static Func<int, string?> Between(int min, int max, string name) =>
        value => value >= min && value <= max ? null : $"{name} must be between {min} and {max}.";
}
