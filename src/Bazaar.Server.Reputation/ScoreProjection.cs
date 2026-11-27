using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Reputation;

/// <summary>
/// The default <see cref="IScoreProjection"/>: reads evidence and runs the pure <see cref="ScoreMath"/>. No snapshot rows are
/// written yet (they need the weights_version rows, which the settings work package owns), so every read is current.
/// </summary>
public sealed class ScoreProjection(IEvidenceSource evidence, WeightPolicy policy, TimeProvider time) : IScoreProjection
{
    public async Task<ScoreView> GetAsync(string agentId, string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var items = await evidence.ReadAsync(agentId, tag, cancellationToken).ConfigureAwait(false);
        return new ScoreView(agentId, tag, ScoreMath.Compute(items, policy, time.GetUtcNow()), items.Count);
    }

    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string tag, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var agents = await evidence.AgentsWithEvidenceAsync(tag, cancellationToken).ConfigureAwait(false);
        var views = new List<Suggestion>(agents.Count);
        foreach (var agent in agents)
        {
            var view = await GetAsync(agent, tag, cancellationToken).ConfigureAwait(false);
            views.Add(new Suggestion(agent, view.Score, view.EvidenceCount));
        }

        return [.. views.OrderByDescending(s => s.Score.Lcb).ThenBy(s => s.AgentId, StringComparer.Ordinal).Take(limit)];
    }
}

/// <summary>
/// Reads evidence from the ledger tables (common/0013): outcome rows attributed to the agent on the tag, and the agent's
/// authored PRs' peer reviews on the tag. Only the newest review of each reviewer's chain counts (a superseded review is skipped),
/// and the 1 to 5 overall is rescaled to [0,1] as (overall - 1) / 4. Reviewer reliability and per-reviewer rescaling are
/// calibration work (package 5), so reliability is 1.0 here.
/// </summary>
public sealed class PgEvidenceSource(NpgsqlDataSource dataSource) : IEvidenceSource
{
    public async Task<IReadOnlyList<Evidence>> ReadAsync(string agentId, string tag, CancellationToken cancellationToken)
    {
        var items = new List<Evidence>();
        await using (var outcomes = dataSource.CreateCommand("""
            SELECT o.kind, o.x, o.occurred_at
            FROM outcome_attribution a JOIN outcome_event o ON o.id = a.outcome_id
            WHERE a.agent_id = @agent AND a.tag = @tag
            """))
        {
            outcomes.Parameters.AddWithValue("agent", agentId);
            outcomes.Parameters.AddWithValue("tag", tag);
            await using var reader = await outcomes.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new Evidence(KindFor(reader.GetString(0)), (double)reader.GetDecimal(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        await using (var reviews = dataSource.CreateCommand("""
            SELECT r.overall, r.created_at
            FROM review r
            WHERE r.author_agent_id = @agent AND r.tag = @tag
              AND NOT EXISTS (SELECT 1 FROM review n WHERE n.supersedes_id = r.id)
            """))
        {
            reviews.Parameters.AddWithValue("agent", agentId);
            reviews.Parameters.AddWithValue("tag", tag);
            await using var reader = await reviews.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new Evidence(EvidenceKind.PeerReview, (reader.GetInt16(0) - 1) / 4.0, reader.GetFieldValue<DateTimeOffset>(1)));
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<string>> AgentsWithEvidenceAsync(string tag, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT agent_id FROM outcome_attribution WHERE tag = @tag
            UNION
            SELECT author_agent_id FROM review WHERE tag = @tag
            """);
        cmd.Parameters.AddWithValue("tag", tag);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var agents = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(reader.GetString(0));
        }

        return agents;
    }

    internal static EvidenceKind KindFor(string kind) => kind switch
    {
        "ci_first_run" => EvidenceKind.CiFirstRun,
        "revert" => EvidenceKind.Revert,
        "post_merge_defect" => EvidenceKind.PostMergeDefect,
        "owner_accept" => EvidenceKind.OwnerAccept,
        "owner_reject" => EvidenceKind.OwnerReject,
        "manual" => EvidenceKind.Manual,
        _ => throw new InvalidOperationException($"Unknown outcome kind '{kind}'."),
    };
}

/// <summary>Composition for the score projection.</summary>
public static class ScoreProjectionRegistration
{
    /// <summary>Registers the projection over the ledger. Needs an <c>NpgsqlDataSource</c>; without one nothing is registered.</summary>
    public static IServiceCollection AddBazaarScoreProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(WeightPolicy.Default);
        services.TryAddSingleton<IEvidenceSource>(sp => sp.GetService<NpgsqlDataSource>() is { } ds ? new PgEvidenceSource(ds) : new EmptyEvidenceSource());
        services.TryAddSingleton<IScoreProjection, ScoreProjection>();
        return services;
    }

    private sealed class EmptyEvidenceSource : IEvidenceSource
    {
        public Task<IReadOnlyList<Evidence>> ReadAsync(string agentId, string tag, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Evidence>>([]);

        public Task<IReadOnlyList<string>> AgentsWithEvidenceAsync(string tag, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
