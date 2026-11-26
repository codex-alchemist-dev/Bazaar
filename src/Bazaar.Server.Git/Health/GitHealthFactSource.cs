using Bazaar.Server.Health;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Git.Health;

/// <summary>Limits for the forge reads the health feeder makes (doc 25 WP1). A forge call costs API quota, so each is bounded.</summary>
public sealed record GitHealthOptions
{
    /// <summary>Pages of PRs read per project and day, newest created first.</summary>
    public int MaxPages { get; init; } = 3;

    /// <summary>PRs per page, 1 to 100.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>The most PRs per project and day whose checks are read.</summary>
    public int MaxChecksPerDay { get; init; } = 50;
}

/// <summary>
/// The Git module's contribution to project health (doc 25, Health and Analytics; doc 21 forge contract). The Bazaar repository id is
/// the health project id. For the UTC day it reads the repo's PRs through <see cref="IForgeRegistry"/>, then:
/// <list type="bullet">
/// <item>a PR updated that day whose checks have all finished is one CI run, passed when no check failed (skipped checks do not fail
/// it); a PR with a pending check is not a completed run and is left out;</item>
/// <item>a merged PR updated that day whose title starts with "Revert" is a revert.</item>
/// </list>
/// The forge gives no check timestamps, so the PR's update time stands in for the run's completion time. Forge errors propagate, so the
/// collection job retries. An unknown repository id or provider is a gap (empty facts), never a zero.
/// </summary>
public sealed class GitHealthFactSource(IForgeRegistry forges, IGitRepositoryDirectory directory, GitHealthOptions? options = null) : IHealthFactSource
{
    private readonly GitHealthOptions _options = options ?? new GitHealthOptions();

    public async Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var info = await directory.FindAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (info is null || !forges.ProviderIds.Contains(info.Repo.Provider, StringComparer.OrdinalIgnoreCase))
            return HealthFacts.Empty;
        var forge = forges.Resolve(info.Repo.Provider);

        var todays = new List<PrSummary>();
        var pageSize = Math.Clamp(_options.PageSize, 1, 100);
        for (var page = 1; page <= Math.Max(1, _options.MaxPages); page++)
        {
            var result = await forge.ListPrsAsync(info.Repo, new ListPrsQuery(null, page, pageSize), cancellationToken).ConfigureAwait(false);
            todays.AddRange(result.Items.Where(p => DateOnly.FromDateTime(p.UpdatedAt.UtcDateTime) == day));
            if (!result.HasMore)
                break;
        }

        var runs = new List<CiRunFact>();
        foreach (var pr in todays.OrderBy(p => p.UpdatedAt).ThenBy(p => p.Pr.Number).Take(Math.Max(0, _options.MaxChecksPerDay)))
        {
            var checks = await forge.GetChecksAsync(pr.Pr, pr.HeadSha, cancellationToken).ConfigureAwait(false);
            if (checks.Count == 0 || checks.Any(c => c.State == CheckState.Pending))
                continue;
            runs.Add(new CiRunFact($"{pr.Pr.FullName}#{pr.Pr.Number}@{Short(pr.HeadSha)}", pr.UpdatedAt, checks.All(c => c.State != CheckState.Failure)));
        }

        var reverts = todays
            .Where(p => p.State == PrState.Merged && p.Title.StartsWith("Revert", StringComparison.OrdinalIgnoreCase))
            .Select(p => new RevertFact(p.HeadSha, p.UpdatedAt))
            .ToList();

        return new HealthFacts { CiRuns = runs, Reverts = reverts };
    }

    private static string Short(string sha) => sha.Length <= 7 ? sha : sha[..7];
}

/// <summary>Lists every registered repository as a health project, by its Bazaar repository id.</summary>
public sealed class GitHealthProjectSource(IGitRepositoryDirectory directory) : IHealthProjectSource
{
    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        [.. (await directory.ListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.RepositoryId)];
}
