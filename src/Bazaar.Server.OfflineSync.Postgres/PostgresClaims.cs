namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>The write-id claim shared by every merge state (<c>offline_write_claim</c>). Runs on the write's transaction.</summary>
internal static class PostgresClaims
{
    private const string _claim = "INSERT INTO offline_write_claim (write_id) VALUES ($1) ON CONFLICT (write_id) DO NOTHING";

    /// <summary>
    /// Claims a write id. Returns false when it was claimed before. A claim by a transaction that has not committed yet makes this
    /// call wait for that transaction, so a concurrent replay of the same write sees the final answer.
    /// </summary>
    public static async ValueTask<bool> TryClaimAsync(PostgresAccess db, Guid writeId, CancellationToken cancellationToken)
    {
        var statement = await db.PrepareAsync(_claim, requireScope: true, cancellationToken).ConfigureAwait(false);
        await using (statement.ConfigureAwait(false))
        {
            statement.Add(writeId);
            return await statement.Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }
}
