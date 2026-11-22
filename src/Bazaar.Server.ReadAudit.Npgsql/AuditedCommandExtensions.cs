using System.Data.Common;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>Opt-in declarations on commands made from an <see cref="AuditedDbDataSource"/> or one of its connections.</summary>
public static class AuditedCommandExtensions
{
    /// <summary>
    /// Declares the object type the command reads. Pass <paramref name="personalIdColumn"/> when the rows are personal objects
    /// and that result column holds each object's id. Returns the command for chaining.
    /// </summary>
    public static T DeclareRead<T>(this T command, string objectType, string? personalIdColumn = null) where T : DbCommand
    {
        ArgumentNullException.ThrowIfNull(command);
        Audited(command).Declaration = new ReadDeclaration(objectType, personalIdColumn);
        return command;
    }

    /// <summary>Excludes this command from read audit. For the audit log's own queries. Returns the command for chaining.</summary>
    public static T SkipReadAudit<T>(this T command) where T : DbCommand
    {
        ArgumentNullException.ThrowIfNull(command);
        Audited(command).SkipAudit = true;
        return command;
    }

    private static AuditedDbCommand Audited(DbCommand command) =>
        command as AuditedDbCommand
        ?? throw new ArgumentException("The command was not made by an audited data source, so it cannot carry a read declaration.", nameof(command));
}
