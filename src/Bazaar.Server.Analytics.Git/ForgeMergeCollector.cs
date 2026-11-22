using Bazaar.Server.Git;

namespace Bazaar.Server.Analytics.Git;

/// <summary>Maps a forge login to the Bazaar agent that did the work. An unmapped login returns null and its PRs are not counted.</summary>
public interface IForgeAuthorResolver
{
    string? AgentFor(string provider, string authorLogin);
}

/// <summary>A fixed login-to-agent table, read from <c>Bazaar:Analytics:ForgeAuthors:{login}</c> = agent id. Logins match case-insensitively.</summary>
public sealed class ConfiguredForgeAuthorResolver(IReadOnlyDictionary<string, string> map) : IForgeAuthorResolver
{
    private readonly Dictionary<string, string> _map = new(map, StringComparer.OrdinalIgnoreCase);

    public string? AgentFor(string provider, string authorLogin) => _map.GetValueOrDefault(authorLogin);
}

/// <summary>
/// Counts merged pull requests straight from the forge (doc 52), for installs where the Kanban "Done on a task with a PR link" signal
/// is not the merge record. It lists each registered repo's merged PRs through <see cref="IForgeProvider"/> (read only) and credits the
/// agent the author login maps to. The forge reports a PR's last update, not its merge time, so a merged PR counts when that time falls
/// in the window: a late edit to an old merged PR can move it. Pages are capped so a big repo cannot stall a read.
/// </summary>
public sealed class ForgeMergeCollector(
    IGitRepositoryDirectory repos,
    IForgeRegistry forges,
    IForgeAuthorResolver authors,
    int maxPagesPerRepo = 5) : IWorkEventCollector
{
    private const int _pageSize = 100;

    public async Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);

        var events = new List<AgentWorkEvent>();
        foreach (var info in await repos.ListAsync(ct).ConfigureAwait(false))
        {
            var forge = forges.Resolve(info.Repo.Provider);
            for (var page = 1; page <= maxPagesPerRepo; page++)
            {
                var result = await forge.ListPrsAsync(info.Repo, new ListPrsQuery(PrState.Merged, page, _pageSize), ct).ConfigureAwait(false);
                foreach (var pr in result.Items)
                {
                    if (pr.State == PrState.Merged && window.Contains(pr.UpdatedAt) && authors.AgentFor(info.Repo.Provider, pr.AuthorLogin) is { } agent)
                    {
                        events.Add(new AgentWorkEvent(agent, AgentWorkEventKind.PrMerged, pr.UpdatedAt));
                    }
                }

                if (!result.HasMore)
                {
                    break;
                }
            }
        }

        return events;
    }
}
