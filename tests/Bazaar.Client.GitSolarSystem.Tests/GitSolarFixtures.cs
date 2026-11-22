using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Tests;

/// <summary>Small hand-written histories for the solar-system tests. No network, no I/O.</summary>
internal static class GitSolarFixtures
{
    public static DateTimeOffset At(int minute) => new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    public static GitSolarCommit Commit(string sha, string branch, int minute, params string[] parents) =>
        new(sha, parents, branch, At(minute), sha);

    /// <summary>main: A, M (merge of B); feature: B. Comet B to M, plain M to A and B to A.</summary>
    public static GitSolarSnapshot MergeWithComet(long version = 1) =>
        new(
            version,
            "main",
            [new GitSolarBranch("main", "M"), new GitSolarBranch("feature", "B")],
            [
                Commit("A", "main", 1),
                Commit("B", "feature", 2, "A"),
                Commit("M", "main", 3, "A", "B"),
            ],
            [
                new GitSolarEdge("M", "A", IsComet: false),
                new GitSolarEdge("B", "A", IsComet: false),
                new GitSolarEdge("B", "M", IsComet: true),
            ]);

    /// <summary>A linear history of <paramref name="count"/> commits on main, with the given sha order and times.</summary>
    public static GitSolarSnapshot Linear(params (string Sha, int Minute)[] commits) =>
        new(
            1,
            "main",
            [new GitSolarBranch("main", commits[^1].Sha)],
            commits.Select(c => Commit(c.Sha, "main", c.Minute)).ToArray(),
            []);

    public static LayoutFrame Frame(long version, long seq, params float[] xyz) =>
        new(version, seq, "focus", xyz);
}
