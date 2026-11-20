namespace Bazaar.Ex;

/// <summary>
/// Where a mnemonic assignment comes from, lowest to highest precedence. A higher tier wins a clash.
/// </summary>
public enum MnemonicTier
{
    /// <summary>A third-party module's default mnemonic.</summary>
    Module = 0,

    /// <summary>A first-party default shipped with the client.</summary>
    BuiltIn = 1,

    /// <summary>A company default. The person may change it.</summary>
    CompanyDefault = 2,

    /// <summary>The person's own remap.</summary>
    Person = 3,

    /// <summary>A company lock. Only the owner may override it.</summary>
    CompanyLocked = 4,

    /// <summary>The owner's remap, which outranks a company lock. Set by the resolver, never by the caller.</summary>
    OwnerPerson = 5,
}

/// <summary>
/// One mnemonic for one command, with its source. <see cref="Alias"/> assignments add a second
/// mnemonic for a command that already has a primary one; they claim the mnemonic only.
/// </summary>
public sealed record MnemonicAssignment(
    string CommandId,
    string Title,
    string Mnemonic,
    MnemonicTier Tier,
    string Source,
    bool Alias = false)
{
    /// <summary>The source label for first-party defaults.</summary>
    public const string BuiltInSource = "bazaar";

    /// <summary>
    /// Turns the commands loaded client modules contributed into built-in assignments: one for each command's mnemonic and
    /// one alias assignment for each of its aliases. Overrides and conflicts are resolved later by <see cref="MnemonicResolver"/>.
    /// </summary>
    public static IReadOnlyList<MnemonicAssignment> FromContributions(IEnumerable<Bazaar.Contracts.Modules.ClientCommandContribution> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var result = new List<MnemonicAssignment>();
        foreach (var command in commands)
        {
            result.Add(new MnemonicAssignment(command.Id, command.Title, command.Mnemonic, MnemonicTier.BuiltIn, BuiltInSource));
            result.AddRange(command.Aliases.Select(alias => new MnemonicAssignment(command.Id, command.Title, alias, MnemonicTier.BuiltIn, BuiltInSource, Alias: true)));
        }

        return result;
    }
}
