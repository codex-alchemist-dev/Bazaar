namespace Bazaar.Server.Memory.Consolidation.Fakes;

/// <summary>In-memory <see cref="IEpisodeSource"/> for tests. Episodes stay until a run marks them consumed.</summary>
public sealed class InMemoryEpisodeSource : IEpisodeSource
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<EpisodeNote>> _episodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _consumed = new(StringComparer.Ordinal);

    /// <summary>Adds an episode for an agent.</summary>
    public void Add(string agentId, EpisodeNote episode)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(episode);

        lock (_sync)
        {
            if (!_episodes.TryGetValue(agentId, out var list))
            {
                list = [];
                _episodes[agentId] = list;
            }

            list.Add(episode);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EpisodeNote>> ReadSinceLastRunAsync(string agentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var consumed = _consumed.TryGetValue(agentId, out var ids) ? ids : [];
            var unread = _episodes.TryGetValue(agentId, out var list)
                ? list.Where(e => !consumed.Contains(e.Id)).ToArray()
                : [];
            return Task.FromResult<IReadOnlyList<EpisodeNote>>(unread);
        }
    }

    /// <inheritdoc />
    public Task MarkConsumedAsync(string agentId, IReadOnlyList<string> episodeIds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(episodeIds);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_consumed.TryGetValue(agentId, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                _consumed[agentId] = ids;
            }

            foreach (var id in episodeIds)
            {
                ids.Add(id);
            }
        }

        return Task.CompletedTask;
    }
}
