using Npgsql;

namespace Bazaar.Server.Skimmer.Data;

/// <summary>Builds commands on the transaction's connection when there is one, else on a pooled connection.</summary>
internal static class SkimmerCommands
{
    public static NpgsqlCommand Create(NpgsqlDataSource dataSource, NpgsqlTransaction? tx, string sql) =>
        tx is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, tx.Connection, tx);
}
