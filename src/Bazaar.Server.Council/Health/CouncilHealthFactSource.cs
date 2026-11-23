using Bazaar.Server.Health;
using Npgsql;

namespace Bazaar.Server.Council.Health;

/// <summary>
/// The Council's contribution to project health (doc 25 morale signal <c>vote_reversals</c>, PROPOSED definition). The health
/// project id is the GC's <c>project</c> metadata slug (doc 01 GC metadata): a vote belongs to the project of the GC it was opened
/// in. A vote counts as a reversal on the UTC day it was decided when at least one voter's follow-up choice differs from that
/// voter's independent choice. A GC with no project slug feeds nothing.
/// </summary>
public sealed class CouncilHealthFactSource(NpgsqlDataSource dataSource) : IHealthFactSource
{
    private const string _reversalsSql = """
        SELECT v.vote_id, t.at
        FROM council_vote v
        JOIN gc g ON g.gc_id = v.gc_id
        JOIN council_transition t ON t.vote_id = v.vote_id AND t.to_state = 'decided'
        WHERE g.metadata ->> 'project' = @project
          AND t.at >= @from AND t.at < @to
          AND EXISTS (
              SELECT 1
              FROM council_ballot f
              JOIN council_ballot i ON i.vote_id = f.vote_id AND i.voter_id = f.voter_id AND i.phase = 'independent'
              WHERE f.vote_id = v.vote_id AND f.phase = 'followup' AND f.choice <> i.choice)
        ORDER BY t.at, v.vote_id
        """;

    public async Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        await using var cmd = dataSource.CreateCommand(_reversalsSql);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("from", start);
        cmd.Parameters.AddWithValue("to", start.AddDays(1));

        var reversals = new List<VoteReversalFact>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            reversals.Add(new VoteReversalFact(reader.GetGuid(0).ToString("D"), reader.GetFieldValue<DateTimeOffset>(1)));

        return new HealthFacts { VoteReversals = reversals };
    }
}

/// <summary>Every project slug that has a Council vote in one of its GCs is a health project.</summary>
public sealed class CouncilHealthProjectSource(NpgsqlDataSource dataSource) : IHealthProjectSource
{
    private const string _projectsSql = """
        SELECT DISTINCT g.metadata ->> 'project'
        FROM council_vote v
        JOIN gc g ON g.gc_id = v.gc_id
        WHERE btrim(coalesce(g.metadata ->> 'project', '')) <> ''
        ORDER BY 1
        """;

    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_projectsSql);
        var projects = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            projects.Add(reader.GetString(0));
        return projects;
    }
}
