namespace Bazaar.Server.BrowserPool.Abstractions;

/// <summary>One thing a pool VM image lacks. Never carries file contents.</summary>
/// <param name="Id">A stable id, for example <c>program:bwrap</c>. Safe to log and to show in a refusal.</param>
/// <param name="Reason">A plain reason a person can act on.</param>
public sealed record PoolReadinessFailure(string Id, string Reason);

/// <summary>
/// An optional check that the pool VM can run sessions at all (installed programs, directories, the AppArmor profile for <c>bwrap</c>).
/// The pool asks every registered instance before it opens a session and refuses when any returns a failure or throws. With none
/// registered the pool behaves as before.
/// </summary>
public interface IPoolReadinessCheck
{
    /// <summary>The failures found now; empty means ready.</summary>
    Task<IReadOnlyList<PoolReadinessFailure>> CheckAsync(CancellationToken ct);
}
