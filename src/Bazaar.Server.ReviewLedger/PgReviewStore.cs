using Bazaar.Server.ReviewLedger.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.ReviewLedger;

/// <summary>
/// Postgres store over <c>review</c> (common/0013, append-only). A transaction-scoped advisory lock keyed on the PR serialises
/// reviews for one PR, so the reviewer cap holds under concurrent writers.
/// </summary>
public sealed class PgReviewStore(NpgsqlDataSource dataSource) : IReviewStore
{
    public async Task<ReviewRecordResult> AppendAsync(ReviewRecord review, int maxReviewersPerPr, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@pr, 0))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue("pr", review.PrRef);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (review.SupersedesId is { } old)
        {
            await using var find = new NpgsqlCommand("SELECT pr_ref, reviewer_agent_id FROM review WHERE id = @id", connection, tx);
            find.Parameters.AddWithValue("id", old);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ReviewRecordResult(null, ReviewRefusal.SupersedesUnknown);
            }

            if (reader.GetString(0) != review.PrRef || reader.GetString(1) != review.ReviewerAgentId)
            {
                return new ReviewRecordResult(null, ReviewRefusal.SupersedesMismatch);
            }
        }
        else
        {
            await using var count = new NpgsqlCommand("""
                SELECT count(DISTINCT reviewer_agent_id) FILTER (WHERE reviewer_agent_id <> @reviewer),
                       bool_or(reviewer_agent_id = @reviewer)
                FROM review WHERE pr_ref = @pr
                """, connection, tx);
            count.Parameters.AddWithValue("pr", review.PrRef);
            count.Parameters.AddWithValue("reviewer", review.ReviewerAgentId);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var others = reader.GetInt64(0);
            var already = !reader.IsDBNull(1) && reader.GetBoolean(1);
            if (!already && others >= maxReviewersPerPr)
            {
                return new ReviewRecordResult(null, ReviewRefusal.TooManyReviewers);
            }
        }

        var id = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO review (id, pr_ref, card_id, reviewer_agent_id, author_agent_id, reviewer_family, author_family, blinded,
                                tag, overall, rubric, rationale_ref, supersedes_id, created_at)
            VALUES (@id, @pr, @card, @reviewer, @author, @rf, @af, @blinded, @tag, @overall, @rubric, @rationale, @supersedes, @at)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("pr", review.PrRef);
            insert.Parameters.AddWithValue("card", (object?)review.CardId ?? DBNull.Value);
            insert.Parameters.AddWithValue("reviewer", review.ReviewerAgentId);
            insert.Parameters.AddWithValue("author", review.AuthorAgentId);
            insert.Parameters.AddWithValue("rf", (object?)review.ReviewerFamily ?? DBNull.Value);
            insert.Parameters.AddWithValue("af", (object?)review.AuthorFamily ?? DBNull.Value);
            insert.Parameters.AddWithValue("blinded", review.Blinded);
            insert.Parameters.AddWithValue("tag", review.Tag);
            insert.Parameters.AddWithValue("overall", (short)review.Overall);
            insert.Parameters.Add(new NpgsqlParameter("rubric", NpgsqlDbType.Jsonb) { Value = (object?)review.RubricJson ?? DBNull.Value });
            insert.Parameters.AddWithValue("rationale", (object?)review.RationaleRef ?? DBNull.Value);
            insert.Parameters.AddWithValue("supersedes", (object?)review.SupersedesId ?? DBNull.Value);
            insert.Parameters.AddWithValue("at", createdAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReviewRecordResult(id, ReviewRefusal.None);
    }
}
