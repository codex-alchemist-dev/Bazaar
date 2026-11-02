namespace Bazaar.Server.Vault.Links;

/// <summary>
/// One document as the link resolver sees it: what the Vault index holds for a document.
/// <see cref="HeadingSlugs"/> are already slugged (see <see cref="VaultSlug"/>); <see cref="BlockIds"/> are without the leading <c>^</c>.
/// </summary>
public sealed record VaultIndexEntry(
    Guid DocId,
    string Path,
    string? Title,
    IReadOnlyList<string> Aliases,
    string? Project,
    IReadOnlyList<string> HeadingSlugs,
    IReadOnlyList<string> BlockIds);
