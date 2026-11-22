using Bazaar.Server.GitSolarSystem.Abstractions;

namespace Bazaar.Server.GitSolarSystem.Tests;

/// <summary>Returns a fixed history per repository, or null for an unknown one. Counts reads.</summary>
internal sealed class FakeHistorySource : IGitSolarHistorySource
{
    private readonly Dictionary<string, GitSolarHistory> _repos = new(StringComparer.Ordinal);

    public int Reads { get; private set; }

    public FakeHistorySource With(string owner, string repo, GitSolarHistory history)
    {
        _repos[owner + "/" + repo] = history;
        return this;
    }

    public Task<GitSolarHistory?> ReadAsync(string provider, string owner, string repo, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(_repos.GetValueOrDefault(owner + "/" + repo));
    }
}

/// <summary>Records every call and returns positions where node i sits at (i, -i).</summary>
internal sealed class FakeLayout : IGitSolarLayout
{
    public List<(int NodeCount, int CoreIndex, GitSolarLayoutOptions Options)> Calls { get; } = [];

    public float[] Layout(int nodeCount, IReadOnlyList<(int From, int To)> edges, int coreIndex, GitSolarLayoutOptions options)
    {
        Calls.Add((nodeCount, coreIndex, options));
        var xy = new float[nodeCount * 2];
        for (var i = 0; i < nodeCount; i++)
        {
            xy[i * 2] = i;
            xy[i * 2 + 1] = -i;
        }

        return xy;
    }
}

/// <summary>Settings with the shipped defaults, optionally overridden.</summary>
internal sealed record FixedSettings : IGitSolarSettings
{
    public GitSolarLayoutMode LayoutMode { get; init; } = GitSolarLayoutMode.Deterministic;

    public GitSolarOrbitRadiusMode OrbitRadiusMode { get; init; } = GitSolarOrbitRadiusMode.Age;

    public double MinOrbitRadius { get; init; } = 10;

    public double MaxOrbitRadius { get; init; } = 100;

    public int MaxCommits { get; init; } = 20000;
}

internal static class Hist
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A commit whose time is <paramref name="minute"/> minutes after the epoch.</summary>
    public static GitSolarHistoryCommit C(string sha, int minute, params string[] parents) =>
        new(sha, parents, _epoch.AddMinutes(minute), "subject " + sha);

    public static GitSolarRef Ref(string name, string tip, bool agent = false) => new(name, tip, agent);

    public static GitSolarHistory History(string defaultBranch, IEnumerable<GitSolarRef> refs, IEnumerable<GitSolarHistoryCommit> commits) =>
        new(defaultBranch, refs.ToList(), commits.ToList());

    /// <summary>A linear main history of <paramref name="count"/> commits, c0 oldest, tip main.</summary>
    public static GitSolarHistory Linear(int count)
    {
        var commits = new List<GitSolarHistoryCommit>();
        for (var i = 0; i < count; i++)
        {
            commits.Add(i == 0 ? C("c0", 0) : C("c" + i, i, "c" + (i - 1)));
        }

        return History("main", [Ref("main", "c" + (count - 1))], commits);
    }
}
