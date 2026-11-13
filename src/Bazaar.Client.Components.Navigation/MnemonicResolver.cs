using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Components.Navigation;

/// <summary>A clash that dropped an assignment. <see cref="Key"/> identifies it for log-once.</summary>
public sealed record MnemonicNotice(string Key, string Message);

/// <summary>
/// The effective assignments after precedence. <see cref="Errors"/> lists problems that must
/// stop a save or load: duplicates inside one tier and invalid mnemonics.
/// </summary>
public sealed record MnemonicResolution(
    IReadOnlyList<MnemonicAssignment> Effective,
    IReadOnlyList<MnemonicNotice> Notices,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Builds the validated key table the router uses. Null when the resolution has errors.</summary>
    public MnemonicTable? BuildTable(IEnumerable<KeyStroke> paletteKeys)
    {
        if (!IsValid)
        {
            return null;
        }

        var result = MnemonicTable.Create(
            paletteKeys,
            Effective.Select(a => new MnemonicBinding(a.Mnemonic, a.CommandId)));
        return result.Table;
    }
}

/// <summary>
/// Resolves module defaults, first-party defaults, company defaults and locks, and person remaps
/// into one set of mnemonics. Pure: no I/O and no clock.
/// </summary>
/// <remarks>
/// Precedence: company locks, then the person's remaps, then company defaults, then first-party
/// defaults, then module defaults. Only the owner may override a company lock. A clash inside one
/// tier is an error. A clash across tiers drops the lower assignment and records a notice, except
/// that a default whose command was remapped above it is dropped silently, because that is the
/// intended effect of a remap.
/// </remarks>
public static class MnemonicResolver
{
    public static MnemonicResolution Resolve(IEnumerable<MnemonicAssignment> assignments, bool actorIsOwner = false)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var errors = new List<string>();
        var notices = new List<MnemonicNotice>();
        var effective = new List<MnemonicAssignment>();
        var byMnemonic = new Dictionary<string, MnemonicAssignment>(StringComparer.Ordinal);
        var byCommand = new Dictionary<string, MnemonicAssignment>(StringComparer.Ordinal);

        var ordered = assignments
            .Select((assignment, index) => (assignment, index))
            .OrderByDescending(item => EffectiveTier(item.assignment, actorIsOwner))
            .ThenBy(item => item.index)
            .Select(item => item.assignment);

        foreach (var assignment in ordered)
        {
            var tier = EffectiveTier(assignment, actorIsOwner);
            var problem = ValidateMnemonic(assignment.Mnemonic);
            if (problem is not null)
            {
                errors.Add($"Mnemonic for '{assignment.CommandId}' is invalid: {problem}");
                continue;
            }

            byMnemonic.TryGetValue(assignment.Mnemonic, out var mnemonicOwner);

            // Only a primary assignment claims its command. An alias claims just its mnemonic.
            MnemonicAssignment? commandOwner = null;
            if (!assignment.Alias)
            {
                byCommand.TryGetValue(assignment.CommandId, out commandOwner);
            }

            if (mnemonicOwner is null && commandOwner is null)
            {
                Claim(assignment, byMnemonic, byCommand, effective);
                continue;
            }

            var blocker = mnemonicOwner ?? commandOwner!;
            var blockerTier = EffectiveTier(blocker, actorIsOwner);
            if (blockerTier == tier)
            {
                errors.Add(
                    $"Mnemonic '{assignment.Mnemonic}' for '{assignment.CommandId}' clashes with '{blocker.CommandId}' in the same scope.");
                continue;
            }

            var remappedDefault = commandOwner is not null
                && assignment.Tier is MnemonicTier.Module or MnemonicTier.BuiltIn;
            if (!remappedDefault)
            {
                notices.Add(new MnemonicNotice(
                    $"{assignment.CommandId}|{assignment.Mnemonic}|{blocker.CommandId}",
                    $"Mnemonic '{assignment.Mnemonic}' for '{assignment.CommandId}' from {assignment.Source} is not used: "
                    + $"'{blocker.CommandId}' from {blocker.Source} takes it."));
            }
        }

        return new MnemonicResolution(effective.AsReadOnly(), notices.AsReadOnly(), errors.AsReadOnly());
    }

    /// <summary>The tier an assignment competes at. The owner's remaps outrank company locks.</summary>
    public static MnemonicTier EffectiveTier(MnemonicAssignment assignment, bool actorIsOwner) =>
        assignment.Tier == MnemonicTier.Person && actorIsOwner ? MnemonicTier.OwnerPerson : assignment.Tier;

    private static void Claim(
        MnemonicAssignment assignment,
        Dictionary<string, MnemonicAssignment> byMnemonic,
        Dictionary<string, MnemonicAssignment> byCommand,
        List<MnemonicAssignment> effective)
    {
        byMnemonic.Add(assignment.Mnemonic, assignment);
        if (!assignment.Alias)
        {
            byCommand.Add(assignment.CommandId, assignment);
        }

        effective.Add(assignment);
    }

    private static string? ValidateMnemonic(string mnemonic)
    {
        var problem = Mnemonic.Validate(mnemonic);
        if (problem is not null)
        {
            return problem;
        }

        return mnemonic[0] == MnemonicTable.ReservedInsertKey
            ? $"'{mnemonic}' starts with the reserved Insert key '{MnemonicTable.ReservedInsertKey}'."
            : null;
    }
}
