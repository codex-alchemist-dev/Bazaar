using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>The time order the pane list and the default model share: newest commit first.</summary>
internal static class GitSolarTimeline
{
    /// <summary>Indexes into <see cref="GitSolarSnapshot.Commits"/>, newest first. Equal times keep the server's order.</summary>
    public static IReadOnlyList<int> NewestFirst(GitSolarSnapshot snapshot) =>
        snapshot.Commits
            .Select((commit, index) => (commit, index))
            .OrderByDescending(pair => pair.commit.CommittedAt)
            .Select(pair => pair.index)
            .ToArray();
}
