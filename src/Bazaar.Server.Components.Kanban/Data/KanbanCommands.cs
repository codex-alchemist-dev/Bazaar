using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>Builds commands on the transaction's connection when there is one, else on a pooled connection.</summary>
internal static class KanbanCommands
{
    public static NpgsqlCommand Create(NpgsqlDataSource dataSource, NpgsqlTransaction? tx, string sql) =>
        tx is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, tx.Connection, tx);

    public static object Nullable<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    public static object Nullable(string? value) => (object?)value ?? DBNull.Value;
}
