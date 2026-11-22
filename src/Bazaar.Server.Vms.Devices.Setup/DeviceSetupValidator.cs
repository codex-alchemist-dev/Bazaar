using System.Text.Json;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Vms.Devices.Setup;

/// <summary>
/// Checks the device-guest part of a setup plan (doc 62 section 7, doc 45 section 1.3 step 14). Pure: it reads the plan and
/// returns issues. Messages name keys and rules, never the values the operator wrote.
/// </summary>
public static class DeviceSetupValidator
{
    public const string EnabledKey = "vms.devices.enabled";
    public const string AndroidSdkRootKey = "vms.android.sdk_root";
    public const string AndroidAvdHomeKey = "vms.android.avd_home";
    public const string AndroidSystemImageKey = "vms.android.system_image";
    public const string MicroVmStateDirKey = "vms.microvm.state_dir";
    public const string IosRuntimePinKey = "vms.ios.runtime_pin";
    public const string MacOsRestoreImagePinKey = "vms.macos.restore_image_pin";
    public const string MacNodeEndpointKey = "vms.node.mac.endpoint";
    public const string MacNodeKeyPinKey = "vms.node.mac.key_pin";

    public const string AndroidModuleName = "Bazaar.Server.Vms.Android";
    public const string MicroVmModuleName = "Bazaar.Server.Vms.MicroVm";
    public const string IosModuleName = "Bazaar.Server.Vms.Ios";
    public const string MacOsModuleName = "Bazaar.Server.Vms.MacOs";

    private static readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal)
    {
        EnabledKey,
        "vms.devices.idle_stop_minutes",
        "vms.devices.max_concurrent",
        AndroidSdkRootKey,
        AndroidAvdHomeKey,
        AndroidSystemImageKey,
        "vms.android.max_concurrent",
        "vms.ios.max_concurrent",
        IosRuntimePinKey,
        "vms.macos.max_concurrent",
        MacOsRestoreImagePinKey,
        MacNodeEndpointKey,
        MacNodeKeyPinKey,
        "vms.microvm.monitor",
        "vms.microvm.max_concurrent",
        MicroVmStateDirKey,
    };

    private static readonly string[] _prefixes = ["vms.devices.", "vms.android.", "vms.microvm.", "vms.ios.", "vms.macos.", "vms.node.mac."];

    /// <summary>Validates the plan. An empty list means valid.</summary>
    public static IReadOnlyList<SetupValidationIssue> Validate(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<SetupValidationIssue>();

        foreach (var key in plan.Settings.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (_prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) && !_knownKeys.Contains(key))
            {
                issues.Add(Error("settings." + key, "is not a known device provider setting."));
            }
        }

        var enabled = ReadEnabled(plan, issues);
        var android = false;
        var microvm = false;
        var ios = false;
        var macos = false;
        foreach (var kind in enabled)
        {
            switch (kind)
            {
                case "android": android = true; break;
                case "microvm": microvm = true; break;
                case "ios": ios = true; break;
                case "macos": macos = true; break;
                default:
                    issues.Add(Error("settings." + EnabledKey, "lists a kind that is not allowed; use microvm, android, ios or macos."));
                    break;
            }
        }

        if (android)
        {
            RequirePath(plan, AndroidSdkRootKey, issues);
            RequirePath(plan, AndroidAvdHomeKey, issues);
            RequireString(plan, AndroidSystemImageKey, issues);
            RequireModule(plan, AndroidModuleName, "android", issues);
        }

        if (microvm)
        {
            RequirePath(plan, MicroVmStateDirKey, issues);
            RequireModule(plan, MicroVmModuleName, "microvm", issues);
        }

        if (ios || macos)
        {
            // Apple guests run only on a Mac node (doc 62 sections 3 and 6). The wizard shows DeviceSetupText.AppleHardwareNotice; here the node link settings are required.
            RequireString(plan, MacNodeEndpointKey, issues, packageId: false);
            RequireString(plan, MacNodeKeyPinKey, issues, packageId: false);
        }

        if (ios)
        {
            RequireModule(plan, IosModuleName, "ios", issues);
        }

        if (macos)
        {
            RequireModule(plan, MacOsModuleName, "macos", issues);
        }

        return issues;
    }

    /// <summary>True for an explicit absolute path: a rooted POSIX path, a drive path or a UNC path. Not relative, not a profile default.</summary>
    public static bool IsExplicitAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return false;
        }

        var rooted = value.StartsWith('/')
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
        if (!rooted || value.Contains('%') || value.Contains('$') || value.Contains('~'))
        {
            return false;
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(s => s is ".." or "." or ".android" or "AppData");
    }

    private static List<string> ReadEnabled(SetupPlan plan, List<SetupValidationIssue> issues)
    {
        var list = new List<string>();
        if (!plan.Settings.TryGetValue(EnabledKey, out var value))
        {
            return list;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            issues.Add(Error("settings." + EnabledKey, "must be a list of strings."));
            return list;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Add(Error("settings." + EnabledKey, "must be a list of strings."));
                return [];
            }

            list.Add(item.GetString() ?? "");
        }

        return list;
    }

    private static void RequirePath(SetupPlan plan, string key, List<SetupValidationIssue> issues)
    {
        if (!TryString(plan, key, out var text))
        {
            issues.Add(Error("settings." + key, "is required and must be a string."));
        }
        else if (!IsExplicitAbsolutePath(text))
        {
            issues.Add(Error("settings." + key, "must be an explicit absolute path, not relative and not a user-profile default."));
        }
    }

    private static void RequireString(SetupPlan plan, string key, List<SetupValidationIssue> issues, bool packageId = true)
    {
        if (!TryString(plan, key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            issues.Add(Error("settings." + key, "is required and must be a non-empty string."));
        }
        else if (packageId && text.Any(c => c is not (';' or '-' or '_' or '.') && !char.IsAsciiLetterOrDigit(c)))
        {
            issues.Add(Error("settings." + key, "must be an SDK package id."));
        }
    }

    private static bool TryString(SetupPlan plan, string key, out string text)
    {
        text = "";
        if (plan.Settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString() ?? "";
            return true;
        }

        return false;
    }

    private static void RequireModule(SetupPlan plan, string module, string kind, List<SetupValidationIssue> issues)
    {
        if (!plan.Modules.Contains(module, StringComparer.Ordinal))
        {
            issues.Add(Error("modules", $"must list {module} when '{kind}' is enabled in {EnabledKey}."));
        }
    }

    private static SetupValidationIssue Error(string path, string message) => new(SetupIssueSeverity.Error, path, message);
}
