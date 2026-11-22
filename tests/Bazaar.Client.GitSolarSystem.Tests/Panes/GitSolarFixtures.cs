using System.Text.Json;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

/// <summary>Shared data for the pane tests: one small history with a merged branch, and a descriptor builder.</summary>
internal static class GitSolarFixtures
{
    public const string MergeSha = "a1b2c3d4e5f60001";
    public const string FeatureSha = "b1b2c3d4e5f60002";
    public const string BaseSha = "c1b2c3d4e5f60003";

    private static readonly DateTimeOffset _base = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Three commits: the base on main, a feature commit on agent/solar, and the merge back on main (newest).</summary>
    public static GitSolarSnapshot Snapshot(long graphVersion = 1, bool withFeatureOnly = false)
    {
        var commits = new List<GitSolarCommit>
        {
            new(MergeSha, [BaseSha, FeatureSha], "main", _base.AddMinutes(10), "Merge the solar branch"),
            new(FeatureSha, [BaseSha], "agent/solar", _base.AddMinutes(5), "Add solar scene"),
            new(BaseSha, [], "main", _base, "Initial commit"),
        };

        if (withFeatureOnly)
        {
            commits = [commits[1]];
        }

        return new GitSolarSnapshot(
            graphVersion,
            "main",
            [new GitSolarBranch("main", MergeSha), new GitSolarBranch("agent/solar", FeatureSha, IsAgentBranch: true)],
            commits,
            [new GitSolarEdge(MergeSha, BaseSha, IsComet: false), new GitSolarEdge(FeatureSha, MergeSha, IsComet: true)]);
    }

    public static PaneDescriptor Descriptor(string modelJson = "{ \"repository\": \"owner/repo\" }") =>
        new(PaneKinds.GitSolarSystem, "Git · Solar System", string.Empty, JsonSerializer.Deserialize<JsonElement>(modelJson));
}
