using System.Data.Common;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Hive;

namespace Bazaar.Server.Hive.Approvals;

/// <summary>
/// Postgres <see cref="IHiveGrantChangeLedger"/> (<c>hive_pending_change</c>, migration components/hive/0002). Pending changes
/// survive a restart, so an approval the owner gives after one still applies. Adding a ticket twice changes nothing.
/// </summary>
public sealed class PgHiveGrantChangeLedger(DbDataSource dataSource) : IHiveGrantChangeLedger
{
    /// <inheritdoc />
    public async Task AddAsync(Guid ticketId, HiveGrantChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO hive_pending_change (ticket_id, action, from_project, to_project, kind) VALUES (@id, @action, @from, @to, @kind) ON CONFLICT (ticket_id) DO NOTHING");
        Add(cmd, "id", ticketId);
        Add(cmd, "action", change.Action == HiveGrantAction.Set ? "set" : "revoke");
        Add(cmd, "from", change.FromProject);
        Add(cmd, "to", change.ToProject);
        Add(cmd, "kind", HiveKinds.Name(change.Kind));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid TicketId, HiveGrantChange Change)>> SnapshotAsync(CancellationToken ct)
    {
        await using var cmd = HiveCommands.Read(
            dataSource, "SELECT ticket_id, action, from_project, to_project, kind FROM hive_pending_change ORDER BY created_at, ticket_id", "hive_pending_change");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<(Guid, HiveGrantChange)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!HiveKinds.TryParse(reader.GetString(4), out var kind))
            {
                continue;
            }

            var action = reader.GetString(1) == "set" ? HiveGrantAction.Set : HiveGrantAction.Revoke;
            rows.Add((reader.GetGuid(0), new HiveGrantChange(action, reader.GetString(2), reader.GetString(3), kind)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid ticketId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM hive_pending_change WHERE ticket_id = @id");
        Add(cmd, "id", ticketId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
