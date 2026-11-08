using Bazaar.Server.Community.Actions;

namespace Bazaar.Server.Community.Tests.Actions;

/// <summary>
/// In-memory <see cref="ICommunityActionSql"/>. It has no update or delete method, which mirrors the append-only table.
/// Ids count from 1 in insert order.
/// </summary>
internal sealed class InMemoryCommunityActionSql : ICommunityActionSql
{
    private readonly List<CommunityActionRecord> _rows = [];

    public IReadOnlyList<CommunityActionRecord> Rows => _rows;

    public Task<long> InsertAsync(CommunityActionRecord record, CancellationToken ct)
    {
        long id = _rows.Count + 1;
        _rows.Add(record with { Id = id, CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(id) });
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<CommunityActionRecord>> ListForAgentAsync(string agentId, string platform, int limit, CancellationToken ct)
    {
        IReadOnlyList<CommunityActionRecord> rows = _rows
            .Where(r => r.AgentId == agentId && r.Platform == platform)
            .OrderByDescending(r => r.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(rows);
    }
}
