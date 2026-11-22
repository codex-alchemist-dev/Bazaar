using System.Data;
using System.Data.Common;
using Bazaar.Server.ReadAudit.Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>
/// Command helpers for the Hive stores, which run on a <see cref="DbDataSource"/> so the host can give them the audited source
/// (doc 48 section 5.1). Parameter handling matches <c>NpgsqlCommand.Parameters.AddWithValue</c>: the driver infers the type.
/// </summary>
internal static class HiveCommands
{
    /// <summary>Makes a command that reads. When the source is audited the command declares the object type it reads.</summary>
    internal static DbCommand Read(DbDataSource source, string sql, string objectType)
    {
        var command = source.CreateCommand(sql);
        return source is AuditedDbDataSource ? command.DeclareRead(objectType) : command;
    }

    internal static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Adds a nullable timestamp, typed so a null still goes to the server as a timestamptz.</summary>
    internal static void AddTimestamp(DbCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = value is { } v ? v : DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
