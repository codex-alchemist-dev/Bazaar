namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// The indexer hook (doc 18, "Indexing"). The writer calls it after every commit it lands, with the head before and after,
/// so the index can catch up to the new head. The commit is already in git when this runs. If the observer fails, the
/// commit stays and the next catch-up run brings the index up to date, because the index state moves only on success.
/// </summary>
public interface IVaultCommitObserver
{
    Task OnCommitAsync(string oldCommit, string newCommit, CancellationToken ct);
}
