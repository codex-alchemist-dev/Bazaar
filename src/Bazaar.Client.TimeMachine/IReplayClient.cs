namespace Bazaar.Client.TimeMachine;

/// <summary>
/// Client-side port to the Time Machine (doc 26 section 4). The real transport needs a wire change that doc 02 must
/// describe first, so heads bind a fake or nothing until then. Replay is read-only: the only write is <see cref="ForkAsync"/>.
/// </summary>
public interface IReplayClient
{
    /// <summary>The GC rendered at <paramref name="seq"/>. The server clamps a seq above the viewer's grant.</summary>
    /// <exception cref="ReplayClientException">The viewer may not read the GC, or the server failed.</exception>
    Task<ReplayFrameView> GetFrameAsync(Guid gcId, long seq, CancellationToken ct);

    /// <summary>Forks a new project GC at a seq. Idempotent on the request.</summary>
    /// <exception cref="ReplayClientException">The viewer may not read the source, or the server failed.</exception>
    Task<ReplayForkOutcome> ForkAsync(ReplayForkRequest request, CancellationToken ct);
}
