using Bazaar.Server.OutcomeLedger.Abstractions;
using Npgsql;

namespace Bazaar.Server.OutcomeLedger;

/// <summary>
/// Postgres ledger over <c>outcome_event</c> and <c>outcome_attribution</c> (common/0013, append-only). The event insert
/// uses <c>ON CONFLICT (dedupe_key) DO NOTHING</c>, so replays and concurrent appends for one key add one row; the attributions
/// are written in the same transaction.
/// </summary>
public sealed class PgOutcomeLedgerStore(NpgsqlDataSource dataSource) : IOutcomeLedgerStore
{
    public async Task<Guid?> AppendAsync(OutcomeRecord resolved, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var id = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO outcome_event (id, kind, pr_ref, x, w, dedupe_key, source_ref, occurred_at, recorded_at)
            VALUES (@id, @kind, @pr, @x, @w, @key, @source, @occurred, @recorded)
            ON CONFLICT (dedupe_key) DO NOTHING
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("kind", KindText(resolved.Kind));
            insert.Parameters.AddWithValue("pr", resolved.PrRef);
            insert.Parameters.AddWithValue("x", resolved.X);
            insert.Parameters.AddWithValue("w", resolved.W ?? 0m);
            insert.Parameters.AddWithValue("key", resolved.DedupeKey);
            insert.Parameters.AddWithValue("source", (object?)resolved.SourceRef ?? DBNull.Value);
            insert.Parameters.AddWithValue("occurred", resolved.OccurredAt);
            insert.Parameters.AddWithValue("recorded", recordedAt);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }
        }

        foreach (var a in resolved.Attributions)
        {
            await using var attribution = new NpgsqlCommand("""
                INSERT INTO outcome_attribution (outcome_id, agent_id, role, tag, w) VALUES (@id, @agent, @role, @tag, @w)
                """, connection, tx);
            attribution.Parameters.AddWithValue("id", id);
            attribution.Parameters.AddWithValue("agent", a.AgentId);
            attribution.Parameters.AddWithValue("role", a.Role);
            attribution.Parameters.AddWithValue("tag", a.Tag);
            attribution.Parameters.AddWithValue("w", a.W ?? resolved.W ?? 0m);
            await attribution.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal static string KindText(OutcomeKind kind) => kind switch
    {
        OutcomeKind.CiFirstRun => "ci_first_run",
        OutcomeKind.Revert => "revert",
        OutcomeKind.PostMergeDefect => "post_merge_defect",
        OutcomeKind.OwnerAccept => "owner_accept",
        OutcomeKind.OwnerReject => "owner_reject",
        OutcomeKind.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
