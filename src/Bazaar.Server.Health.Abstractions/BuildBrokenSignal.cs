namespace Bazaar.Server.Health;

/// <summary>
/// A project's CI went from passing to failing (doc 24 section 3 build-broken seam). Raised once per red streak: the first failed
/// run after a passed one. <paramref name="RunRef"/> is the failing run's id and <paramref name="At"/> the time it completed.
/// </summary>
public sealed record BuildBroken(string ProjectId, string RunRef, DateTimeOffset At);

/// <summary>
/// Called in process when a project's build breaks. Implementations must be quick and must not assume order against other
/// listeners; a listener that throws is logged and skipped, and never fails the health collection.
/// </summary>
public interface IBuildBrokenListener
{
    Task OnBuildBrokenAsync(BuildBroken signal, CancellationToken cancellationToken = default);
}
