using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Npgsql;

namespace Bazaar.Server.Gc.Consumers;

/// <summary>
/// Sample read-side consumer: the unread count per GC for one principal (docs/design/01, "What happened while I was away").
/// Unread means events after the principal's <c>read_state.last_read_seq</c> that the principal did not write itself.
/// </summary>
/// <remarks>
/// The count is computed on demand from the log through <see cref="IGcLogReader"/>, so the viewer rules apply: a GC the principal
/// may not read raises <see cref="GcAccessDeniedException"/> instead of being counted. The consumer keeps no state of its own.
/// </remarks>
public sealed class UnreadCountConsumer(NpgsqlDataSource dataSource, IGcLogReader reader)
{
    private const string _lastReadSql = "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2";

    /// <summary>The unread events in the GC for the principal. A principal with no read state has read nothing.</summary>
    public async Task<long> CountUnreadAsync(GcPrincipal principal, Guid gcId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var afterSeq = await LoadLastReadSeqAsync(principal.Id, gcId, ct).ConfigureAwait(false);
        long unread = 0;
        while (true)
        {
            var pageSize = 0;
            await foreach (var e in reader.ReadAsync(gcId, afterSeq, IGcLogReader.MaxLimit, principal, ct).ConfigureAwait(false))
            {
                pageSize++;
                afterSeq = e.Seq;
                if (e.AuthorId != principal.Id)
                    unread++;
            }
            if (pageSize == 0)
                return unread;
        }
    }

    private async Task<long> LoadLastReadSeqAsync(Guid principalId, Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_lastReadSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return stored is long seq ? seq : 0;
    }
}
