using System.Collections.Frozen;

namespace Bazaar.Server.Setup;

/// <summary>One part of the plan that a step can apply: a stable id, the plan leaf paths it owns, and the doc 45 section 1.3 steps it belongs to.</summary>
/// <param name="Id">Stable id a step names in <see cref="ISetupStep.Covers"/>, for example <c>owner</c>. Never a value.</param>
/// <param name="PlanPaths">Dotted plan paths. A leaf belongs to the section when it equals a path or sits under it.</param>
/// <param name="DocSteps">The step numbers of doc 45 section 1.3 that apply this part. Used in messages.</param>
public sealed record SetupPlanSection(string Id, IReadOnlyList<string> PlanPaths, IReadOnlyList<int> DocSteps);

/// <summary>
/// The one registry of plan sections (doc 45 sections 1.2 and 1.3). Every leaf of the plan belongs to exactly one section. A step
/// declares the sections it applies in <see cref="ISetupStep.Covers"/>, and the applier refuses a plan that asks for a section no
/// registered step covers. Adding a step later therefore shrinks the unapplied list on its own; nothing else is edited.
/// </summary>
/// <remarks>
/// Why: a setup that cannot apply what the operator asked for must say so before it changes anything. A section the plan leaves at
/// the shipped default is not asked for, so it never counts. Pure: the checks read the plan and the declarations, never a store.
/// </remarks>
public static class SetupPlanSections
{
    /// <summary>The reason a plan is held because of unapplied sections. It is the issue path, so scripts can match it.</summary>
    public const string HoldReason = "unapplied_sections";

    /// <summary>Every section, in doc 45 step order.</summary>
    public static IReadOnlyList<SetupPlanSection> All { get; } =
    [
        new("install.identity", ["install.companyName", "install.companySlug", "install.locale", "install.timezone"], [1]),
        new("install.releaseChannel", ["install.releaseChannel"], [1]),
        new("install.updatePolicy", ["install.updatePolicy"], [2]),
        new("storage", ["storage"], [3]),
        new("database", ["database"], [3, 4]),
        new("custody", ["custody.kekProvider", "custody.kekRef", "custody.auditAnchorTarget"], [5]),
        new("custody.identityKek", ["custody.identityKek"], [5]),
        new("owner", ["owner.displayName", "owner.requireSecondFactor"], [6]),
        new("owner.recovery", ["owner.recovery.bundleRef", "owner.recovery.printedCodes"], [6]),
        new("owner.backupOwners", ["owner.recovery.backupOwners"], [6]),
        new("privacy", ["privacy"], [7]),
        new("backups", ["backups"], [8]),
        new("models", ["models"], [9]),
        new("git", ["custody.mergeCredential"], [10]),
        new("email", ["email"], [11]),
        new("scamMailProtocol", ["scamMailProtocol"], [12]),
        new("company", ["company"], [13]),
        new("modules", ["modules"], [14]),
        new("network", ["network"], [15]),
        new("settings", ["settings"], []),
        new("firstRun", ["firstRun"], []),
    ];

    private static readonly FrozenSet<string> _ids = All.Select(section => section.Id).ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> _defaultLeaves =
        SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(new SetupPlan()));

    /// <summary>True when <paramref name="id"/> names a registered section.</summary>
    public static bool IsKnown(string id) => _ids.Contains(id);

    /// <summary>
    /// The sections the plan asks for: those with at least one leaf that differs from the shipped default plan. A section left
    /// entirely at its default or empty is not asked for. When <paramref name="current"/> is given, a leaf the current state
    /// already holds with the planned value needs no change, so it does not count either.
    /// </summary>
    public static IReadOnlyList<SetupPlanSection> Requested(SetupPlan plan, SetupStateSnapshot? current = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var leaves = SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(plan));
        var requested = new List<SetupPlanSection>();
        foreach (var section in All)
        {
            if (leaves.Any(leaf => Owns(section, leaf.Key) && Differs(leaf) && !Held(current, leaf)))
            {
                requested.Add(section);
            }
        }

        return requested;
    }

    /// <summary>The requested sections that no id in <paramref name="covered"/> names, in doc 45 step order.</summary>
    public static IReadOnlyList<SetupPlanSection> Unapplied(SetupPlan plan, IEnumerable<string> covered, SetupStateSnapshot? current = null)
    {
        ArgumentNullException.ThrowIfNull(covered);
        var coveredSet = covered.ToHashSet(StringComparer.Ordinal);
        return [.. Requested(plan, current).Where(section => !coveredSet.Contains(section.Id))];
    }

    /// <summary>
    /// One line for an issue or a message: the section ids with their doc 45 step numbers, never a value. For example
    /// <c>owner (doc 45 step 6), database (doc 45 steps 3, 4)</c>.
    /// </summary>
    public static string Describe(IEnumerable<SetupPlanSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return string.Join(", ", sections.Select(section => section.DocSteps.Count switch
        {
            0 => section.Id,
            1 => $"{section.Id} (doc 45 step {section.DocSteps[0]})",
            _ => $"{section.Id} (doc 45 steps {string.Join(", ", section.DocSteps)})",
        }));
    }

    /// <summary>Looks up the sections for ids, skipping unknown ids. Keeps registry order.</summary>
    public static IReadOnlyList<SetupPlanSection> ByIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var set = ids.ToHashSet(StringComparer.Ordinal);
        return [.. All.Where(section => set.Contains(section.Id))];
    }

    private static bool Owns(SetupPlanSection section, string leafPath) =>
        section.PlanPaths.Any(path => leafPath.Equals(path, StringComparison.Ordinal) || leafPath.StartsWith(path + ".", StringComparison.Ordinal));

    private static bool Held(SetupStateSnapshot? current, KeyValuePair<string, string> leaf) =>
        current is not null && current.LeafValues.TryGetValue(leaf.Key, out var text) && string.Equals(text, leaf.Value, StringComparison.Ordinal);

    private static bool Differs(KeyValuePair<string, string> leaf) =>
        !_defaultLeaves.TryGetValue(leaf.Key, out var defaultText) || !string.Equals(defaultText, leaf.Value, StringComparison.Ordinal);
}
