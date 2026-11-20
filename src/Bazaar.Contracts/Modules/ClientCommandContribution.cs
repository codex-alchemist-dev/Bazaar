namespace Bazaar.Contracts.Modules;

/// <summary>
/// A command a client module contributes, with the default mnemonic that opens it (doc 41 section 5 item 2 and the
/// client-wide mnemonic navigation section). The id must be one of the manifest's <see cref="ModuleManifest.Commands"/>.
/// The default is only a default: the company and the user can re-code it, and the host resolves conflicts.
/// </summary>
/// <param name="Id">Dotted command id, for example <c>nav.gc.first</c>.</param>
/// <param name="Title">Short label the palette and the mnemonic list show.</param>
/// <param name="Mnemonic">Default typed mnemonic: lowercase letters and digits.</param>
/// <param name="Aliases">Extra mnemonics that open the same command. Empty for none.</param>
public sealed record ClientCommandContribution(string Id, string Title, string Mnemonic, IReadOnlyList<string> Aliases)
{
    /// <summary>Longest mnemonic the contract accepts.</summary>
    public const int MaxMnemonicLength = 32;

    /// <summary>A contribution with no aliases.</summary>
    public ClientCommandContribution(string id, string title, string mnemonic)
        : this(id, title, mnemonic, [])
    {
    }

    /// <summary>True when <paramref name="mnemonic"/> is a well-formed default mnemonic.</summary>
    public static bool IsValidMnemonic(string? mnemonic) =>
        !string.IsNullOrEmpty(mnemonic)
        && mnemonic.Length <= MaxMnemonicLength
        && mnemonic.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
}
