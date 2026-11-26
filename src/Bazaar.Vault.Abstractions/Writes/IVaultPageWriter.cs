namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Creates or updates one Markdown page at a fixed path under an operator-allowed prefix (doc 18, "Page writes"), for
/// modules that own a Vault area, such as Company Memory under <c>company/memory/</c>. Decisions never go through here:
/// they have their own numbered write path.
/// </summary>
public interface IVaultPageWriter
{
    /// <summary>
    /// Writes the page, commits it with the Bazaar trailers and tells the indexer. Idempotent by
    /// <see cref="VaultPageDraft.DocId"/>: the same id at the same path with the same content commits nothing. Throws
    /// <see cref="ArgumentException"/> for a path outside the allowed prefixes or a field the Vault refuses, and
    /// <see cref="InvalidOperationException"/> when the path or the id already belongs to another document.
    /// </summary>
    Task<VaultPageResult> CreateOrUpdatePageAsync(VaultPageDraft draft, CancellationToken ct);
}
