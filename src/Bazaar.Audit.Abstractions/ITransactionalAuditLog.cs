using Npgsql;

namespace Bazaar.Server.Security.Audit;

/// <summary>
/// An <see cref="IAuditLog"/> that can append inside the caller's transaction. A state change and its audit row then
/// commit or roll back together, so a crash cannot leave a change with no audit entry. Refusals that change no state
/// still use <see cref="IAuditLog.AppendAsync"/>.
/// </summary>
public interface ITransactionalAuditLog : IAuditLog
{
    /// <summary>
    /// Appends one entry on <paramref name="conn"/> inside <paramref name="tx"/>. Nothing is visible to other sessions
    /// until the caller commits <paramref name="tx"/>. The caller owns the transaction and must not commit it twice.
    /// </summary>
    Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default);
}
