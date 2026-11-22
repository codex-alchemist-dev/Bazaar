using System.Data.Common;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>
/// A <see cref="DbDataSource"/> that records reads through <see cref="ReadAuditor"/> (doc 48 section 5.1). It wraps an inner
/// source (in production the <c>NpgsqlDataSource</c>; Npgsql 10 has no command interceptor and its data source cannot be
/// wrapped from outside, but <see cref="DbDataSource"/> can). Commands made from it, or from its connections, audit every
/// statement that returns a reader or a scalar, except writes (insert, update, delete). Row counts are known when the reader
/// closes, so the row is recorded then; a failure to record propagates to the caller. Neither SQL text, row content nor the query
/// shape hash is persisted. Batches are not supported and throw, because they would bypass the audit silently.
/// </summary>
public sealed class AuditedDbDataSource : DbDataSource
{
    private readonly DbDataSource _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public AuditedDbDataSource(DbDataSource inner, ReadAuditor auditor, AuditedDataSourceOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        Options = options ?? new AuditedDataSourceOptions();
        DataReadPayload.ValidateRead(new DataRead(Options.UntaggedObjectType, 0));
    }

    /// <summary>The auditor reads are reported to.</summary>
    internal ReadAuditor Auditor { get; }

    internal AuditedDataSourceOptions Options { get; }

    /// <summary>
    /// The inner source, unaudited. For the audit log's own queries and nothing else (doc 48 section 5.1); a caller that reads
    /// personal data through it is a coverage gap.
    /// </summary>
    public DbDataSource Unaudited => _inner;

    /// <inheritdoc />
    public override string ConnectionString => _inner.ConnectionString;

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection() => new AuditedDbConnection(_inner.CreateConnection(), this);

    /// <inheritdoc />
    protected override DbConnection OpenDbConnection()
    {
        var inner = _inner.OpenConnection();
        return new AuditedDbConnection(inner, this);
    }

    /// <inheritdoc />
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var inner = await _inner.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new AuditedDbConnection(inner, this);
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand(string? commandText = null)
    {
        var command = _inner.CreateCommand(commandText);
        return new AuditedDbCommand(command, this, null);
    }

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() =>
        throw new NotSupportedException("Batches are not read-audited yet, so an audited data source refuses them. Use single commands, or the Unaudited source for the audit log's own writes.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
