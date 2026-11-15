using Bazaar.Contracts.Roadmap;
using Npgsql;

namespace Bazaar.RyuseiFuda.Roadmap;

/// <summary>
/// Reads the rows the roadmap needs: the objective and phase documents of one Vault, and the states of the tasks linked to each
/// objective. Read-only; the views in migration <c>roadmap/0001</c> describe the same join.
/// </summary>
public static class RoadmapRepository
{
    private const string _documentsSql = """
        SELECT d.path, d.title, d.frontmatter::text
        FROM vault_docs d
        WHERE d.vault_id = $1 AND d.frontmatter->>'kind' IN ('objective', 'phase')
        ORDER BY d.path
        """;

    private const string _statesSql = """
        SELECT l.ref, t.state
        FROM task_links l
        JOIN tasks t ON t.id = l.task_id
        WHERE l.kind = 'vault_doc' AND l.ref = ANY($1)
        """;

    /// <summary>Loads the roadmap rows of one Vault and classifies them. Empty when the Vault has no roadmap documents.</summary>
    public static async Task<RoadmapLoad> LoadAsync(NpgsqlDataSource dataSource, Guid vaultId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        var rows = new List<RoadmapDocumentRow>();
        await using (var cmd = dataSource.CreateCommand(_documentsSql))
        {
            cmd.Parameters.AddWithValue(vaultId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new RoadmapDocumentRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        // Task links are read for every roadmap path, phases included. Only objective paths match a link, so phases come back with no states.
        var candidatePaths = rows.Select(r => r.Path).ToArray();
        var states = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (candidatePaths.Length > 0)
        {
            await using var cmd = dataSource.CreateCommand(_statesSql);
            cmd.Parameters.AddWithValue(candidatePaths);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (!states.TryGetValue(path, out var list))
                    states[path] = list = new List<string>();
                ((List<string>)list).Add(reader.GetString(1));
            }
        }

        return RoadmapDocumentLoader.Classify(rows, states);
    }
}
