namespace Bazaar.Server.GitSolarSystem.Abstractions;

/// <summary>A branch ref of a repository as the history source reports it.</summary>
/// <param name="Name">Branch name, for example <c>main</c> or <c>agent/wp-12</c>.</param>
/// <param name="TipSha">The commit the branch points at.</param>
/// <param name="IsAgentBranch">True for an <c>agent/&lt;task&gt;</c> branch (a planet of the solar system).</param>
public sealed record GitSolarRef(string Name, string TipSha, bool IsAgentBranch);

/// <summary>One commit as the history source reports it. Parents are in git's parent order (first parent first).</summary>
public sealed record GitSolarHistoryCommit(
    string Sha,
    IReadOnlyList<string> ParentShas,
    DateTimeOffset CommittedAt,
    string Subject = "");

/// <summary>The raw history of one repository, before the solar-system builder runs.</summary>
public sealed record GitSolarHistory(
    string DefaultBranch,
    IReadOnlyList<GitSolarRef> Refs,
    IReadOnlyList<GitSolarHistoryCommit> Commits);

/// <summary>
/// Port: reads the git history of one repository for the Git Solar System. Adapters to the real git index are a follow-up.
/// </summary>
public interface IGitSolarHistorySource
{
    /// <summary>Returns the history, or null when the repository is unknown.</summary>
    Task<GitSolarHistory?> ReadAsync(string provider, string owner, string repo, CancellationToken ct);
}

/// <summary>How an orbit radius follows a branch's last-activity age (<c>constellation.orbit_radius</c>).</summary>
public enum GitSolarOrbitRadiusMode
{
    /// <summary>Radius scales from <see cref="GitSolarLayoutOptions.MinOrbitRadius"/> (youngest) to <see cref="GitSolarLayoutOptions.MaxOrbitRadius"/> (oldest).</summary>
    Age,

    /// <summary>Every body sits at <see cref="GitSolarLayoutOptions.MinOrbitRadius"/>.</summary>
    Equal,
}

/// <summary>Options for one layout run.</summary>
/// <param name="Seed">Fixes the initial ring phases. The same graph, core and seed give the same positions.</param>
/// <param name="OrbitSpacing">Distance between neighbouring rings when no activity ages are given. Must be finite and positive.</param>
/// <param name="OrbitRadiusMode">Age or Equal radius mode. Only read when <paramref name="ActivityAges"/> is given.</param>
/// <param name="MinOrbitRadius">Radius of the innermost body orbit. Must be finite and positive.</param>
/// <param name="MaxOrbitRadius">Radius of the outermost body orbit. Must be finite and at least <paramref name="MinOrbitRadius"/>.</param>
/// <param name="ActivityAges">Seconds (or any single unit) since each node's commit, one entry per node, or null for hop-depth rings.</param>
public sealed record GitSolarLayoutOptions(
    int Seed = 1,
    double OrbitSpacing = 10,
    GitSolarOrbitRadiusMode OrbitRadiusMode = GitSolarOrbitRadiusMode.Age,
    double MinOrbitRadius = 10,
    double MaxOrbitRadius = 100,
    double[]? ActivityAges = null);

/// <summary>
/// Port: lays out a graph around its core. Returns packed x,y per node index (stride 2). Adapters to the constellation
/// orbit layout are a follow-up.
/// </summary>
public interface IGitSolarLayout
{
    float[] Layout(int nodeCount, IReadOnlyList<(int From, int To)> edges, int coreIndex, GitSolarLayoutOptions options);
}

/// <summary>The current Git Solar System settings (doc 19, Settings).</summary>
public enum GitSolarLayoutMode
{
    /// <summary>History does not jitter as it grows (<c>constellation.git_layout = deterministic</c>, the default).</summary>
    Deterministic,

    /// <summary>Layout may re-seed when history changes (<c>constellation.git_layout = force</c>).</summary>
    Force,
}

/// <summary>
/// Port: the current values of the solar-system settings. Defaults: deterministic layout, age orbit radius, radius 10 to 100,
/// and a commit cap of 20000 (<c>git.solar_max_commits</c>).
/// </summary>
public interface IGitSolarSettings
{
    GitSolarLayoutMode LayoutMode { get; }

    GitSolarOrbitRadiusMode OrbitRadiusMode { get; }

    double MinOrbitRadius { get; }

    double MaxOrbitRadius { get; }

    int MaxCommits { get; }
}
