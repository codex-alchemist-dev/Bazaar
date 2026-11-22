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

    public const string AndroidModuleName = "Bazaar.Server.Vms.Android";
    public const string MicroVmModuleName = "Bazaar.Server.Vms.MicroVm";

    private static readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal)
    {
        EnabledKey,
        "vms.devices.idle_stop_minutes",
        "vms.devices.max_concurrent",
        AndroidSdkRootKey,
        AndroidAvdHomeKey,
        AndroidSystemImageKey,
        "vms.android.max_concurrent",
        "vms.microvm.monitor",
        "vms.microvm.max_concurrent",
        MicroVmStateDirKey,
    };

    private static readonly string[] _prefixes = ["vms.devices.", "vms.android.", "vms.microvm."];

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
        foreach (var kind in enabled)
        {
            switch (kind)
            {
                case "android": android = true; break;
                case "microvm": microvm = true; break;
                case "ios" or "macos":
                    issues.Add(Error("settings." + EnabledKey, $"'{kind}' is parked. {DeviceSetupText.AppleHardwareNotice}"));
                    break;
                default:
                    issues.Add(Error("settings." + EnabledKey, "lists a kind that is not allowed; use microvm or android."));
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

    private static void RequireString(SetupPlan plan, string key, List<SetupValidationIssue> issues)
    {
        if (!TryString(plan, key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            issues.Add(Error("settings." + key, "is required and must be a non-empty string."));
        }
        else if (text.Any(c => c is not (';' or '-' or '_' or '.') && !char.IsAsciiLetterOrDigit(c)))
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
