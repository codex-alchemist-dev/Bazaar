using Bazaar.Erasure.Abstractions;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>
/// Erases one person's crash data (doc 43 section 11.3, doc 32 section 9): their reports and screenshots go, and the group
/// summaries stay. The person's key itself is destroyed by the coordinator's key eraser, which also makes any copy of a sealed
/// body in a backup unreadable.
/// </summary>
public sealed class CrashErasureParticipant(ICrashInstallDirectory directory, ICrashErasure erasure) : IErasureParticipant
{
    /// <inheritdoc />
    public string Name => "crash-reports";

    /// <inheritdoc />
    public async Task<ErasureParticipantResult> EraseAsync(Guid personId, CancellationToken ct = default)
    {
        try
        {
            var installs = await directory.GetInstallsAsync(personId, ct).ConfigureAwait(false);
            if (installs.Count == 0)
            {
                return ErasureParticipantResult.NothingToDo();
            }

            var result = await erasure.EraseInstallsAsync(installs, ct).ConfigureAwait(false);
            var removed = (long)result.Reports + result.Attachments;
            return removed == 0 ? ErasureParticipantResult.NothingToDo() : ErasureParticipantResult.Removed(removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErasureParticipantResult.Failed(ex.Message);
        }
    }
}

/// <summary>
/// The directory used until the identity component provides one. It fails closed: nothing is sealed under a person's key by
/// guesswork, and an erasure reports failure instead of claiming there was nothing to do.
/// </summary>
public sealed class UnavailableCrashInstallDirectory : ICrashInstallDirectory
{
    /// <inheritdoc />
    public Task<Guid?> GetOwnerAsync(Guid installId, CancellationToken ct = default) => throw Unavailable();

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> GetInstallsAsync(Guid personId, CancellationToken ct = default) => throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("No install directory is registered; crash reports cannot be tied to a person until the identity component supplies one.");
}
