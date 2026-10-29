using Npgsql;

namespace Bazaar.Server.Skimmer.Data;

/// <summary>SQL-first access to <c>gate_labels</c> (doc 13, "Measurement" and "Feedback"). Labels are append-only in practice: this repository never updates or deletes.</summary>
public sealed class GateLabelRepository(NpgsqlDataSource dataSource)
{
    private const string _insertSql = """
        INSERT INTO gate_labels (id, decision_id, label, source, labeller) VALUES (@id, @decision_id, @label, @source, @labeller)
        """;

    /// <summary>Adds one label to a decision.</summary>
    public async Task InsertAsync(NewGateLabel label, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        await using var cmd = SkimmerCommands.Create(dataSource, tx, _insertSql);
        cmd.Parameters.AddWithValue("id", label.Id);
        cmd.Parameters.AddWithValue("decision_id", label.DecisionId);
        cmd.Parameters.AddWithValue("label", label.Label);
        cmd.Parameters.AddWithValue("source", label.Source);
        cmd.Parameters.AddWithValue("labeller", label.Labeller);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The labels on one decision, oldest first.</summary>
    public async Task<IReadOnlyList<GateLabelRecord>> ListForDecisionAsync(Guid decisionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT id, decision_id, label, source, labeller, created_at FROM gate_labels
            WHERE decision_id = @decision_id
            ORDER BY created_at, id
            """);
        cmd.Parameters.AddWithValue("decision_id", decisionId);
        var result = new List<GateLabelRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new GateLabelRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }
}
