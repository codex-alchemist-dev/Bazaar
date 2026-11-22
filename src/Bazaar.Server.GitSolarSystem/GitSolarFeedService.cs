using Bazaar.Contracts.Graph;
using Bazaar.Server.GitSolarSystem.Abstractions;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>The feed of one repository, and whether its history was cut to the newest commits.</summary>
public sealed record GitSolarFeedResult(GitSolarFeed Feed, bool Truncated);

/// <summary>
/// Builds the solar-system feed (snapshot plus layout frame) for one repository. The core is the default branch tip. Ages
/// feed the layout only in <see cref="GitSolarOrbitRadiusMode.Age"/> mode.
/// </summary>
public sealed class GitSolarFeedService(
    IGitSolarHistorySource history,
    IGitSolarLayout layout,
    IGitSolarSettings settings)
{
    /// <summary>Stride of the packed frame: x, y, z per node (z is always 0).</summary>
    private const int _stride = 3;

    private readonly IGitSolarHistorySource _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly IGitSolarLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly IGitSolarSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Returns the feed, or null when the repository is unknown.</summary>
    public async Task<GitSolarFeedResult?> GetFeedAsync(string provider, string owner, string repo, CancellationToken ct)
    {
        var raw = await _history.ReadAsync(provider, owner, repo, ct);
        if (raw is null)
        {
            return null;
        }

        var truncated = false;
        var cap = _settings.MaxCommits;
        if (raw.Commits.Count > cap)
        {
            truncated = true;
            raw = raw with { Commits = NewestFirstCut(raw, cap) };
        }

        var snapshot = GitSolarSnapshotBuilder.Build(raw);
        var frame = Frame(snapshot);
        return new GitSolarFeedResult(new GitSolarFeed(snapshot, frame), truncated);
    }

    /// <summary>The newest <paramref name="cap"/> commits, in the builder's time-then-sha order.</summary>
    private static List<GitSolarHistoryCommit> NewestFirstCut(GitSolarHistory history, int cap)
    {
        var ordered = history.Commits
            .OrderBy(c => c.CommittedAt)
            .ThenBy(c => c.Sha, StringComparer.Ordinal)
            .ToList();
        return ordered.GetRange(ordered.Count - cap, cap);
    }

    private LayoutFrame Frame(GitSolarSnapshot snapshot)
    {
        var nodeCount = snapshot.Commits.Count;
        var focus = snapshot.Branches.FirstOrDefault(b => b.Name == snapshot.DefaultBranch)?.TipSha ?? string.Empty;
        if (nodeCount == 0)
        {
            return new LayoutFrame(snapshot.GraphVersion, LayoutSeq: 1, FocusId: focus, Xyz: ReadOnlyMemory<float>.Empty);
        }

        var indexBySha = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodeCount; i++)
        {
            indexBySha.TryAdd(snapshot.Commits[i].Sha, i);
        }

        var coreIndex = indexBySha.TryGetValue(focus, out var core) ? core : 0;
        var edges = snapshot.Edges
            .Select(e => (From: indexBySha[e.FromSha], To: indexBySha[e.ToSha]))
            .ToList();

        var options = new GitSolarLayoutOptions(
            Seed: _settings.LayoutMode == GitSolarLayoutMode.Force ? unchecked((int)snapshot.GraphVersion) : 1,
            OrbitRadiusMode: _settings.OrbitRadiusMode,
            MinOrbitRadius: _settings.MinOrbitRadius,
            MaxOrbitRadius: _settings.MaxOrbitRadius,
            ActivityAges: _settings.OrbitRadiusMode == GitSolarOrbitRadiusMode.Age ? ActivityAges(snapshot) : null);

        var xy = _layout.Layout(nodeCount, edges, coreIndex, options);
        var xyz = new float[nodeCount * _stride];
        for (var i = 0; i < nodeCount; i++)
        {
            xyz[i * _stride] = xy[i * 2];
            xyz[i * _stride + 1] = xy[i * 2 + 1];
            xyz[i * _stride + 2] = 0;
        }

        return new LayoutFrame(snapshot.GraphVersion, LayoutSeq: 1, FocusId: focus, Xyz: xyz);
    }

    /// <summary>Seconds from each commit to the newest commit in the snapshot (0 for the newest).</summary>
    private static double[] ActivityAges(GitSolarSnapshot snapshot)
    {
        var newest = snapshot.Commits.Max(c => c.CommittedAt);
        return snapshot.Commits.Select(c => (newest - c.CommittedAt).TotalSeconds).ToArray();
    }
}
