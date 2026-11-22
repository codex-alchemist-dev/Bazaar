using System.Collections.Concurrent;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>In-memory <see cref="IWardStayStore"/>. Fine for tests and a single-process host until the Postgres store lands.</summary>
public sealed class InMemoryWardStayStore : IWardStayStore
{
    private readonly ConcurrentDictionary<Guid, WardStay> _stays = new();

    /// <inheritdoc />
    public Task<WardStay?> GetLatestAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_stays.Values.Where(s => s.AgentId == agentId).OrderByDescending(s => s.AdmittedAt).FirstOrDefault());

    /// <inheritdoc />
    public Task SaveAsync(WardStay stay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stay);
        _stays[stay.Id] = stay;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WardStay>> ListActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WardStay>>([.. _stays.Values.Where(s => s.IsAdmitted).OrderByDescending(s => s.AdmittedAt)]);
}
