using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Manifest;

/// <summary>One validation finding: a stable cause and the manifest field it concerns.</summary>
public sealed record ModuleManifestProblem(string Cause, string Field);

/// <summary>
/// The structural rules of doc 46 section 11, as a pure function. It does not check the signature (the loader does) and does not
/// grant anything. Findings come in a fixed order, so the same manifest always gives the same list.
/// </summary>
public static class ModuleManifestValidator
{
    /// <summary>The health contract version this build understands (doc 46 section 10.4).</summary>
    public const int SupportedContract = 1;

    public const int MaxNameLength = 80;
    public const int MaxVersionLength = 64;
    public const int MaxEntryLength = 128;
    public const int MaxRunbookPathLength = 200;
    public const int MaxDependencies = 64;
    public const int MaxProbes = 128;
    public const int MaxRunbooks = 32;
    public const int MaxPermissions = 64;

    /// <summary>The longest self-test budget (doc 46 section 10.1: never more than 60 s).</summary>
    public const int MaxSelfTestBudgetMs = 60_000;

    private const string _runbookRoot = "runbooks/";
    private const string _runbookExtension = ".json";

    private static readonly string[] _kinds = ["first-party", "third-party"];
    private static readonly string[] _healthSources = ["builtin", "module"];
    private static readonly string[] _schedules = ["off", "hourly", "daily", "weekly"];

    private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Checks a decoded manifest against the section 11 rules. An empty list means it is structurally acceptable.</summary>
    public static IReadOnlyList<ModuleManifestProblem> Validate(ModuleManifest manifest, ModuleProbeCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(catalogue);

        List<ModuleManifestProblem> problems = [];
        CheckIdentity(manifest, problems);
        CheckDependencies(manifest, problems);
        CheckHealth(manifest.Health, problems);
        CheckProbes(manifest.Probes, catalogue, problems);
        CheckRunbooks(manifest.Runbooks, problems);
        CheckPermissions(manifest.Permissions, problems);
        return problems;
    }

    /// <summary>True when <paramref name="path"/> is a relative <c>runbooks/*.json</c> path that cannot leave the package folder.</summary>
    public static bool IsConfinedRunbookPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxRunbookPathLength)
        {
            return false;
        }

        if (!path.StartsWith(_runbookRoot, StringComparison.Ordinal) || !path.EndsWith(_runbookExtension, StringComparison.Ordinal))
        {
            return false;
        }

        // Splitting on '/' only: a backslash, colon or any other separator fails the segment pattern, so it cannot hide a traversal.
        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (!ModuleManifestPatterns.PathSegment().IsMatch(segment))
            {
                return false;
            }

            string stem = segment.Contains('.', StringComparison.Ordinal) ? segment[..segment.IndexOf('.', StringComparison.Ordinal)] : segment;
            if (_reservedDeviceNames.Contains(stem))
            {
                return false;
            }
        }

        // Needs a file name after the folder, and the extension alone is not a name.
        return segments.Length >= 2 && segments[^1].Length > _runbookExtension.Length;
    }

    private static void CheckIdentity(ModuleManifest manifest, List<ModuleManifestProblem> problems)
    {
        if (manifest.Id is null || !KodoExportPatterns.ModuleId().IsMatch(manifest.Id))
        {
            problems.Add(new(ModuleManifestCauses.IdInvalid, "id"));
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > MaxNameLength || manifest.Name.Any(char.IsControl))
        {
            problems.Add(new(ModuleManifestCauses.NameInvalid, "name"));
        }

        if (manifest.Kind is null || !_kinds.Contains(manifest.Kind, StringComparer.Ordinal))
        {
            problems.Add(new(ModuleManifestCauses.KindInvalid, "kind"));
        }

        if (manifest.Version is null || manifest.Version.Length > MaxVersionLength || !ModuleManifestPatterns.SemVer().IsMatch(manifest.Version))
        {
            problems.Add(new(ModuleManifestCauses.VersionInvalid, "version"));
        }

        if (manifest.KodoContract != SupportedContract)
        {
            problems.Add(new(ModuleManifestCauses.ContractUnsupported, "kodo.contract"));
        }
    }

    private static void CheckDependencies(ModuleManifest manifest, List<ModuleManifestProblem> problems)
    {
        ModuleManifestDependencies? dependencies = manifest.Dependencies;
        if (dependencies is null)
        {
            return;
        }

        HashSet<string> hard = CheckDependencyList(dependencies.Hard, "dependencies.hard", manifest.Id, problems);
        HashSet<string> soft = CheckDependencyList(dependencies.Soft, "dependencies.soft", manifest.Id, problems);
        if (hard.Overlaps(soft))
        {
            problems.Add(new(ModuleManifestCauses.DependencyConflict, "dependencies"));
        }
    }

    private static HashSet<string> CheckDependencyList(IReadOnlyList<string>? list, string field, string? ownId, List<ModuleManifestProblem> problems)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (list is null)
        {
            return seen;
        }

        if (list.Count > MaxDependencies)
        {
            problems.Add(new(ModuleManifestCauses.TooManyEntries, field));
            return seen;
        }

        foreach (string? id in list)
        {
            if (id is null || !KodoExportPatterns.ModuleId().IsMatch(id) || id == ownId)
            {
                problems.Add(new(ModuleManifestCauses.DependencyInvalid, field));
            }
            else if (!seen.Add(id))
            {
                problems.Add(new(ModuleManifestCauses.DependencyDuplicate, field));
            }
        }

        return seen;
    }

    private static void CheckHealth(ModuleManifestHealth? health, List<ModuleManifestProblem> problems)
    {
        // Doc 46 section 11: a module with no health block does not load.
        if (health is null)
        {
            problems.Add(new(ModuleManifestCauses.HealthMissing, "health"));
            return;
        }

        if (health.Liveness is null || !_healthSources.Contains(health.Liveness, StringComparer.Ordinal))
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.liveness"));
        }

        if (health.Readiness is null || !_healthSources.Contains(health.Readiness, StringComparer.Ordinal))
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.readiness"));
        }

        ModuleManifestSelfTest? selfTest = health.SelfTest;
        if (selfTest is null)
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.selfTest"));
            return;
        }

        bool quickOk = selfTest.QuickBudgetMs is >= 1 and <= MaxSelfTestBudgetMs;
        bool fullOk = selfTest.FullBudgetMs is >= 1 and <= MaxSelfTestBudgetMs;
        if (!quickOk)
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.selfTest.quickBudgetMs"));
        }

        if (!fullOk || (quickOk && selfTest.QuickBudgetMs > selfTest.FullBudgetMs))
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.selfTest.fullBudgetMs"));
        }

        if (selfTest.Scheduled is null || !_schedules.Contains(selfTest.Scheduled, StringComparer.Ordinal))
        {
            problems.Add(new(ModuleManifestCauses.HealthInvalid, "health.selfTest.scheduled"));
        }
    }

    private static void CheckProbes(IReadOnlyList<string>? probes, ModuleProbeCatalogue catalogue, List<ModuleManifestProblem> problems)
    {
        if (probes is null)
        {
            return;
        }

        if (probes.Count > MaxProbes)
        {
            problems.Add(new(ModuleManifestCauses.TooManyEntries, "probes"));
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? id in probes)
        {
            if (id is null || id.Length > 64 || !KodoExportPatterns.ComponentId().IsMatch(id) || KodoExportPatterns.InstanceIdLike().IsMatch(id))
            {
                problems.Add(new(ModuleManifestCauses.ProbeInvalid, "probes"));
            }
            else if (!seen.Add(id))
            {
                problems.Add(new(ModuleManifestCauses.ProbeDuplicate, "probes"));
            }
            else if (!catalogue.Contains(id))
            {
                problems.Add(new(ModuleManifestCauses.ProbeUnknown, "probes"));
            }
        }
    }

    private static void CheckRunbooks(IReadOnlyList<string>? runbooks, List<ModuleManifestProblem> problems)
    {
        if (runbooks is null)
        {
            return;
        }

        if (runbooks.Count > MaxRunbooks)
        {
            problems.Add(new(ModuleManifestCauses.TooManyEntries, "runbooks"));
            return;
        }

        // Compared without case: two paths that differ only by case are one file on Windows.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in runbooks)
        {
            if (!IsConfinedRunbookPath(path))
            {
                problems.Add(new(ModuleManifestCauses.RunbookPathInvalid, "runbooks"));
            }
            else if (!seen.Add(path!))
            {
                problems.Add(new(ModuleManifestCauses.RunbookDuplicate, "runbooks"));
            }
        }
    }

    private static void CheckPermissions(IReadOnlyList<string>? permissions, List<ModuleManifestProblem> problems)
    {
        if (permissions is null)
        {
            return;
        }

        if (permissions.Count > MaxPermissions)
        {
            problems.Add(new(ModuleManifestCauses.TooManyEntries, "permissions"));
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? permission in permissions)
        {
            if (permission is null || permission.Length > MaxEntryLength || !ModuleManifestPatterns.Permission().IsMatch(permission))
            {
                problems.Add(new(ModuleManifestCauses.PermissionInvalid, "permissions"));
            }
            else if (!seen.Add(permission))
            {
                problems.Add(new(ModuleManifestCauses.PermissionDuplicate, "permissions"));
            }
        }
    }
}
