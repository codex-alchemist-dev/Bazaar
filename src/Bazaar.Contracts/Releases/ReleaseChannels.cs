namespace Bazaar.Contracts.Releases;

/// <summary>
/// The five release channels (owner, 2026-10-25; docs/VERSIONING.md section 4). The wire value is the exact
/// <c>update.channel</c> setting value (doc 30 section 5), so the names are case-sensitive.
/// </summary>
public static class ReleaseChannels
{
    /// <summary>One major, security and critical fixes only; versions carry no pre-release suffix.</summary>
    public const string Ltsc = "LTSC";

    /// <summary>Tags with no pre-release suffix.</summary>
    public const string Stable = "Stable";

    /// <summary>Tags with a <c>-beta.N</c> or <c>-rc.N</c> suffix.</summary>
    public const string Beta = "Beta";

    /// <summary>Tags with an <c>-alpha.N</c> suffix.</summary>
    public const string Alpha = "Alpha";

    /// <summary>CI builds of <c>main</c>, <c>-dev.N</c> suffix. Owner-only; never offered to other operators.</summary>
    public const string LatestCommit = "Latest-Commit";

    /// <summary>Every known channel name.</summary>
    public static IReadOnlyList<string> All { get; } = [Ltsc, Stable, Beta, Alpha, LatestCommit];

    /// <summary>True when <paramref name="channel"/> is exactly one of the known names.</summary>
    public static bool IsKnown(string? channel) => channel is not null && All.Contains(channel);
}
