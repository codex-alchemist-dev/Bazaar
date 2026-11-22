namespace Bazaar.Contracts.Modules;

/// <summary>
/// Which host machines a module can run on (owner, 2026-11-16). Hyper-V and Windows Sandbox exist only on a Windows host
/// with the feature enabled; KVM exists only on Linux. A module declares this as typed, versioned metadata, and the host
/// checks it at install and at provider selection. Every list must be non-empty: an empty list would match nothing.
/// </summary>
/// <param name="SchemaVersion">Version of this record's shape. Only <see cref="CurrentSchemaVersion"/> is accepted.</param>
/// <param name="OperatingSystems">Host operating systems the module runs on, from <see cref="HostCompatibility.KnownOperatingSystems"/>.</param>
/// <param name="Architectures">Host CPU architectures, from <see cref="HostCompatibility.KnownArchitectures"/>.</param>
/// <param name="RequiredFeatures">Host features that must be present, from <see cref="HostCompatibility.KnownFeatures"/>. May be empty.</param>
public sealed record HostRequirements(
    int SchemaVersion,
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<string> RequiredFeatures)
{
    /// <summary>The only host requirements schema this build understands.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>What one host machine offers, probed locally. Feature names come from <see cref="HostCompatibility.KnownFeatures"/>.</summary>
/// <param name="OperatingSystem">The host OS, one of <see cref="HostCompatibility.KnownOperatingSystems"/>.</param>
/// <param name="Architecture">The host CPU architecture, one of <see cref="HostCompatibility.KnownArchitectures"/>.</param>
/// <param name="Features">Features that are enabled on this host right now.</param>
public sealed record HostFacts(string OperatingSystem, string Architecture, IReadOnlySet<string> Features);

/// <summary>One reason a host cannot run a module. Code is stable for tests and the status page.</summary>
/// <param name="Code">Stable code: <c>schema-version</c>, <c>os-unsupported</c>, <c>arch-unsupported</c>, <c>feature-missing</c>, or a validation code.</param>
/// <param name="Message">Human-readable detail.</param>
public sealed record HostCompatibilityIssue(string Code, string Message);

/// <summary>
/// Pure checks for <see cref="HostRequirements"/>: vocabulary validation for a manifest, and the match against one host.
/// Never throws and does no I/O.
/// </summary>
public static class HostCompatibility
{
    /// <summary>Host operating systems a module may name. Android is a client host only.</summary>
    public static readonly IReadOnlySet<string> KnownOperatingSystems = new HashSet<string>(StringComparer.Ordinal)
    {
        "windows",
        "linux",
        "android",
    };

    /// <summary>Host CPU architectures a module may name.</summary>
    public static readonly IReadOnlySet<string> KnownArchitectures = new HashSet<string>(StringComparer.Ordinal)
    {
        "x64",
        "arm64",
    };

    /// <summary>Host features a module may require. The list grows by reviewed change only.</summary>
    public static readonly IReadOnlySet<string> KnownFeatures = new HashSet<string>(StringComparer.Ordinal)
    {
        "hyperv",
        "windows-sandbox",
        "kvm",
        "android-emulator",
        "microvm",
    };

    /// <summary>Checks the declaration itself: schema version, non-empty lists, known names, no duplicates. Pure.</summary>
    public static IReadOnlyList<HostCompatibilityIssue> ValidateRequirements(HostRequirements? requirements)
    {
        if (requirements is null)
        {
            return [new HostCompatibilityIssue("missing-field", "host requirements are required.")];
        }

        var issues = new List<HostCompatibilityIssue>();
        if (requirements.SchemaVersion != HostRequirements.CurrentSchemaVersion)
        {
            issues.Add(new HostCompatibilityIssue("schema-version", $"schemaVersion must be {HostRequirements.CurrentSchemaVersion}."));
        }

        CheckNames(requirements.OperatingSystems, KnownOperatingSystems, "operatingSystems", requiredNonEmpty: true, issues);
        CheckNames(requirements.Architectures, KnownArchitectures, "architectures", requiredNonEmpty: true, issues);
        CheckNames(requirements.RequiredFeatures, KnownFeatures, "requiredFeatures", requiredNonEmpty: false, issues);
        return issues;
    }

    /// <summary>
    /// Returns the reasons <paramref name="facts"/> cannot run a module with <paramref name="requirements"/>. An empty list means compatible.
    /// An invalid declaration is itself reported, so a malformed requirement can never match.
    /// </summary>
    public static IReadOnlyList<HostCompatibilityIssue> Check(HostRequirements? requirements, HostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var issues = ValidateRequirements(requirements).ToList();
        if (requirements is null || issues.Count > 0)
        {
            return issues;
        }

        if (!requirements.OperatingSystems.Contains(facts.OperatingSystem, StringComparer.Ordinal))
        {
            issues.Add(new HostCompatibilityIssue("os-unsupported", $"needs {string.Join(" or ", requirements.OperatingSystems)}; this host is {facts.OperatingSystem}."));
        }

        if (!requirements.Architectures.Contains(facts.Architecture, StringComparer.Ordinal))
        {
            issues.Add(new HostCompatibilityIssue("arch-unsupported", $"needs {string.Join(" or ", requirements.Architectures)}; this host is {facts.Architecture}."));
        }

        foreach (var feature in requirements.RequiredFeatures)
        {
            if (!facts.Features.Contains(feature))
            {
                issues.Add(new HostCompatibilityIssue("feature-missing", $"required host feature '{feature}' is not enabled."));
            }
        }

        return issues;
    }

    private static void CheckNames(IReadOnlyList<string>? names, IReadOnlySet<string> known, string path, bool requiredNonEmpty, List<HostCompatibilityIssue> issues)
    {
        if (names is null)
        {
            issues.Add(new HostCompatibilityIssue("missing-field", $"{path} is required."));
            return;
        }

        if (requiredNonEmpty && names.Count == 0)
        {
            issues.Add(new HostCompatibilityIssue("empty-list", $"{path} must name at least one value."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is null || !known.Contains(name))
            {
                issues.Add(new HostCompatibilityIssue("unknown-value", $"'{name}' is not a known value for {path}."));
            }
            else if (!seen.Add(name))
            {
                issues.Add(new HostCompatibilityIssue("duplicate", $"'{name}' is listed twice in {path}."));
            }
        }
    }
}
