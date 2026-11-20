using Bazaar.Client.Input;

namespace Bazaar.Ex;

/// <summary>The outcome of a remap or reset. A rejected request leaves the document as it was and says why.</summary>
public sealed record MnemonicRemapResult(bool Accepted, MnemonicOverrideDocument Document, string? Reason)
{
    public static MnemonicRemapResult Accept(MnemonicOverrideDocument document) => new(true, document, null);

    public static MnemonicRemapResult Reject(MnemonicOverrideDocument unchanged, string reason) => new(false, unchanged, reason);
}

/// <summary>
/// Applies one person's remap to the saved overrides. Pure: it returns the new document and the caller saves it.
/// A remap is refused when the text is not a valid mnemonic, when the command is locked by the company (the owner may
/// still change it), or when another command already uses the mnemonic. It never takes a mnemonic from another command,
/// so a remap can only add a way to reach a command, never orphan one. Moving a command back to its default drops the override.
/// </summary>
public static class MnemonicRemapper
{
    public static MnemonicRemapResult TryRemap(
        IEnumerable<MnemonicAssignment> baseAssignments,
        MnemonicOverrideDocument current,
        string commandId,
        string mnemonic,
        IEnumerable<KeyStroke> paletteKeys,
        bool actorIsOwner = false)
    {
        ArgumentNullException.ThrowIfNull(baseAssignments);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(mnemonic);
        ArgumentNullException.ThrowIfNull(paletteKeys);

        var palette = paletteKeys.ToList();
        var defaults = baseAssignments.Where(a => a.Tier != MnemonicTier.Person).ToList();
        var primary = defaults.FirstOrDefault(a => a.CommandId == commandId && !a.Alias);
        if (primary is null)
        {
            return MnemonicRemapResult.Reject(current, $"'{commandId}' is not a command you can remap.");
        }

        var problem = Mnemonic.Validate(mnemonic);
        if (problem is not null)
        {
            return MnemonicRemapResult.Reject(current, problem);
        }

        if (mnemonic[0] == MnemonicTable.ReservedInsertKey)
        {
            return MnemonicRemapResult.Reject(current, $"'{mnemonic}' starts with the reserved Insert key '{MnemonicTable.ReservedInsertKey}'.");
        }

        if (primary.Tier == MnemonicTier.CompanyLocked && !actorIsOwner)
        {
            return MnemonicRemapResult.Reject(current, $"'{primary.Title}' is locked by your company; only the owner can change it.");
        }

        var titles = defaults
            .GroupBy(a => a.CommandId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);

        var holder = Effective(defaults, current, titles, actorIsOwner)
            .FirstOrDefault(a => a.Mnemonic == mnemonic && !(a.CommandId == commandId && !a.Alias));
        if (holder is not null)
        {
            return MnemonicRemapResult.Reject(current, $"'{mnemonic}' is already used by '{holder.Title}'.");
        }

        var others = current.Overrides.Where(o => o.CommandId != commandId).ToList();
        var next = mnemonic == primary.Mnemonic
            ? others
            : [.. others, new MnemonicOverride(commandId, mnemonic)];
        var document = current with { Overrides = next };

        var resolution = MnemonicResolver.Resolve(Combine(defaults, document, titles), actorIsOwner);
        if (!resolution.IsValid || resolution.BuildTable(palette) is null)
        {
            var reason = resolution.Errors is [var first, ..]
                ? first
                : $"'{mnemonic}' conflicts with the palette or another mnemonic.";
            return MnemonicRemapResult.Reject(current, reason);
        }

        return MnemonicRemapResult.Accept(document);
    }

    /// <summary>Drops the person's remap of a command, so the default applies again. A command with no remap is a no-op.</summary>
    public static MnemonicRemapResult Reset(MnemonicOverrideDocument current, string commandId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var kept = current.Overrides.Where(o => o.CommandId != commandId).ToList();
        return MnemonicRemapResult.Accept(current with { Overrides = kept });
    }

    private static List<MnemonicAssignment> Combine(
        IReadOnlyList<MnemonicAssignment> defaults,
        MnemonicOverrideDocument document,
        IReadOnlyDictionary<string, string> titles) =>
        [.. defaults, .. FileMnemonicOverrideStore.ToAssignments(document, titles)];

    private static IReadOnlyList<MnemonicAssignment> Effective(
        IReadOnlyList<MnemonicAssignment> defaults,
        MnemonicOverrideDocument document,
        IReadOnlyDictionary<string, string> titles,
        bool actorIsOwner) =>
        MnemonicResolver.Resolve(Combine(defaults, document, titles), actorIsOwner).Effective;
}
