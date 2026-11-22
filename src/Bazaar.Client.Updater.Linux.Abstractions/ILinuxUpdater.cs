namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// The Linux client's in-place updater (doc 30 sections 4 and 7). Releases sit in <c>releases/&lt;version&gt;</c> behind a
/// <c>current</c> symlink, so switching and rolling back are each one atomic step.
/// </summary>
public interface ILinuxUpdater
{
    /// <summary>Reads the manifest and decides whether an update is on offer. Changes nothing on disk.</summary>
    Task<UpdateResult> CheckAsync(CancellationToken ct);

    /// <summary>Checks, downloads, verifies hash and signature, extracts and switches <c>current</c>. A failed step leaves the installed build untouched.</summary>
    Task<UpdateResult> ApplyAsync(CancellationToken ct);

    /// <summary>Points <c>current</c> back at the previous release.</summary>
    UpdateResult Rollback();
}
