namespace Bazaar.Server.RepoMap;

/// <summary>
/// Builds a token-bounded outline of the symbols in a worktree, for an agent to read before it searches or edits.
/// This is the Read capability of the coding harness.
/// </summary>
public interface IRepoMapBuilder
{
    /// <summary>The capability class a caller needs to use this builder.</summary>
    const string CapabilityClass = "Read";

    /// <summary>
    /// Builds the map. An invalid request throws <see cref="ArgumentException"/>.
    /// A failed file listing throws <see cref="RepoToolException"/>.
    /// </summary>
    Task<RepoMapResult> BuildAsync(RepoMapRequest request, CancellationToken ct);
}
