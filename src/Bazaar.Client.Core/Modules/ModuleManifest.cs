using System.Text.RegularExpressions;

namespace Bazaar.Client.Core.Modules;

/// <summary>How a module is loaded (doc 41 section 10, decision 2, owner 2026-11-12: out-of-process for non-shipped modules).</summary>
public enum ModuleIsolation
{
    /// <summary>Compiled into the head. Used for shipped modules.</summary>
    InProcessCompiled,

    /// <summary>Runs in its own process and talks over the module protocol v1 (named pipe on Windows, Unix socket elsewhere, stdio as fallback).</summary>
    OutOfProcess,
}

/// <summary>
/// The manifest every client module declares (doc 41 section 8). The host enforces it the same way for every module,
/// first-party or not (doc 41 section 10, decision 1). Records are immutable; <see cref="ModuleManifestValidator"/> checks them.
/// </summary>
public sealed record ModuleManifest(
    string Id,
    string Version,
    int ContractVersion,
    IReadOnlyList<string> PaneKinds,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> SettingKeys,
    IReadOnlyList<string> Capabilities,
    ModuleIsolation Isolation);

/// <summary>One manifest problem. Path is a JSON pointer; Code is stable for tests.</summary>
public sealed record ModuleManifestIssue(string Path, string Code, string Message);

/// <summary>Result of validating a manifest. Manifest is null whenever any issue was found.</summary>
public sealed record ModuleManifestResult(ModuleManifest? Manifest, IReadOnlyList<ModuleManifestIssue> Issues)
{
    public bool IsValid => Manifest is not null;
}

/// <summary>
/// Checks a manifest. Pure. Rules: dotted lower-case id, SemVer version, contract version 1 or more, pane kinds and
/// commands in the dotted-name form, capabilities from the known vocabulary, no duplicates, bounded counts.
/// The capability vocabulary is the one in doc 41 section 8 (for example <c>wire:gc.history</c>, <c>storage:local</c>, <c>device:notify</c>).
/// </summary>
public static partial class ModuleManifestValidator
{
    public const int MaxListItems = 64;
    public const int MaxContractVersion = 1000;

    /// <summary>Capability names a manifest may request. Unknown capabilities are rejected; the list grows by reviewed change only.</summary>
    public static readonly IReadOnlySet<string> KnownCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "wire:gc.history",
        "wire:gc.post",
        "wire:kanban.read",
        "wire:kanban.write",
        "wire:vault.read",
        "wire:constellation.read",
        "storage:local",
        "device:notify",
    };

    [GeneratedRegex("^[a-z][a-z0-9_-]*(\\.[a-z0-9_-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedNamePattern();

    [GeneratedRegex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();

    [GeneratedRegex("^[a-z][a-z0-9_-]*(\\.[a-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();

    /// <summary>Validates a manifest. Never throws.</summary>
    public static ModuleManifestResult Validate(ModuleManifest? manifest)
    {
        if (manifest is null)
        {
            return new ModuleManifestResult(null, [new ModuleManifestIssue("", "missing", "A manifest is required.")]);
        }

        var issues = new List<ModuleManifestIssue>();

        if (manifest.Id is null || !DottedNamePattern().IsMatch(manifest.Id) || manifest.Id.Length > 64)
        {
            issues.Add(new ModuleManifestIssue("/id", "bad-id", "Id must be a dotted lower-case name of up to 64 characters, such as bazaar.gc."));
        }

        if (manifest.Version is null || !SemVerPattern().IsMatch(manifest.Version))
        {
            issues.Add(new ModuleManifestIssue("/version", "bad-version", "Version must be SemVer."));
        }

        if (manifest.ContractVersion < 1 || manifest.ContractVersion > MaxContractVersion)
        {
            issues.Add(new ModuleManifestIssue("/contractVersion", "bad-contract-version", $"contractVersion must be 1 to {MaxContractVersion}."));
        }

        CheckList(manifest.PaneKinds, "/paneKinds", DottedNamePattern(), "bad-pane-kind", issues);
        CheckList(manifest.Commands, "/commands", DottedNamePattern(), "bad-command", issues);
        CheckList(manifest.SettingKeys, "/settingKeys", SettingKeyPattern(), "bad-setting-key", issues);

        if (manifest.Capabilities is null)
        {
            issues.Add(new ModuleManifestIssue("/capabilities", "missing-field", "capabilities is required; use an empty list."));
        }
        else
        {
            if (manifest.Capabilities.Count > MaxListItems)
            {
                issues.Add(new ModuleManifestIssue("/capabilities", "too-many", $"At most {MaxListItems} capabilities."));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < manifest.Capabilities.Count; i++)
            {
                string cap = manifest.Capabilities[i];
                string path = $"/capabilities/{i}";
                if (!KnownCapabilities.Contains(cap))
                {
                    issues.Add(new ModuleManifestIssue(path, "unknown-capability", $"'{cap}' is not a known capability."));
                }
                else if (!seen.Add(cap))
                {
                    issues.Add(new ModuleManifestIssue(path, "duplicate", "Each capability may be listed once."));
                }
            }
        }

        if (!Enum.IsDefined(manifest.Isolation))
        {
            issues.Add(new ModuleManifestIssue("/isolation", "bad-isolation", "isolation must be InProcessCompiled or OutOfProcess."));
        }

        return issues.Count == 0 ? new ModuleManifestResult(manifest, []) : new ModuleManifestResult(null, issues);
    }

    private static void CheckList(IReadOnlyList<string>? list, string path, Regex pattern, string code, List<ModuleManifestIssue> issues)
    {
        if (list is null)
        {
            issues.Add(new ModuleManifestIssue(path, "missing-field", "A list is required; use an empty list."));
            return;
        }

        if (list.Count > MaxListItems)
        {
            issues.Add(new ModuleManifestIssue(path, "too-many", $"At most {MaxListItems} items."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            string item = list[i];
            if (item is null || !pattern.IsMatch(item) || item.Length > 64)
            {
                issues.Add(new ModuleManifestIssue($"{path}/{i}", code, $"'{item}' is not a valid name."));
            }
            else if (!seen.Add(item))
            {
                issues.Add(new ModuleManifestIssue($"{path}/{i}", "duplicate", $"'{item}' is listed twice."));
            }
        }
    }
}
