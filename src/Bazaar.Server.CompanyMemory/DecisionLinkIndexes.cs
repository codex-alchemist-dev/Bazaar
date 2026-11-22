using Bazaar.Server.Knowledge.CompanyMemory;
using Npgsql;

namespace Bazaar.Server.CompanyMemory;

/// <summary>The index in memory: the fallback without a database, and the fake for tests.</summary>
public sealed class InMemoryDecisionLinkIndex : IDecisionLinkIndex
{
    private readonly object _gate = new();
    private IReadOnlyList<DecisionLink> _links = [];

    public Task ReplaceAllAsync(IReadOnlyList<DecisionLink> links, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(links);
        lock (_gate)
        {
            _links = [.. links];
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DecisionLink>> LinksTouchingAsync(string path, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DecisionLink>>([.. _links
                .Where(l => l.FromDoc == path || l.ToDoc == path)
                .OrderBy(l => l.FromDoc, StringComparer.Ordinal)
                .ThenBy(l => l.ToDoc, StringComparer.Ordinal)]);
        }
    }
}

/// <summary>The index in Postgres (<c>decision_link</c>, migration components/company_memory/0001).</summary>
public sealed class PgDecisionLinkIndex(NpgsqlDataSource dataSource) : IDecisionLinkIndex
{
    public async Task ReplaceAllAsync(IReadOnlyList<DecisionLink> links, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(links);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var clear = new NpgsqlCommand("DELETE FROM decision_link", connection, transaction))
        {
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var link in links)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO decision_link (from_doc, to_doc, relation, status, from_missing, to_missing)
                VALUES (@from, @to, @relation, @status, @from_missing, @to_missing)
                ON CONFLICT DO NOTHING
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("from", link.FromDoc);
            insert.Parameters.AddWithValue("to", link.ToDoc);
            insert.Parameters.AddWithValue("relation", link.Relation);
            insert.Parameters.AddWithValue("status", (object?)link.Status ?? DBNull.Value);
            insert.Parameters.AddWithValue("from_missing", link.FromMissing);
            insert.Parameters.AddWithValue("to_missing", link.ToMissing);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DecisionLink>> LinksTouchingAsync(string path, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            SELECT from_doc, to_doc, relation, status, from_missing, to_missing
            FROM decision_link
            WHERE from_doc = @path OR to_doc = @path
            ORDER BY from_doc COLLATE "C", to_doc COLLATE "C"
            """);
        cmd.Parameters.AddWithValue("path", path);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var links = new List<DecisionLink>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            links.Add(new DecisionLink(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        }

        return links;
    }
}
