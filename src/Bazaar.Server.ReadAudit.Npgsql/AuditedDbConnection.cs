using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>A connection that hands out audited commands. Everything else is the inner connection.</summary>
internal sealed class AuditedDbConnection(DbConnection inner, AuditedDbDataSource source) : DbConnection
{
    internal DbConnection Inner { get; } = inner;

    internal AuditedDbDataSource Source { get; } = source;

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        set => Inner.ConnectionString = value;
    }

    public override string Database => Inner.Database;

    public override string DataSource => Inner.DataSource;

    public override string ServerVersion => Inner.ServerVersion;

    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

    public override void Close() => Inner.Close();

    public override Task CloseAsync() => Inner.CloseAsync();

    public override void Open() => Inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Inner.BeginTransaction(isolationLevel);

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await Inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    protected override DbCommand CreateDbCommand() => new AuditedDbCommand(Inner.CreateCommand(), Source, this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
