using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>
/// A command that reports its reads. A reader or scalar result is audited once the statement has run; a statement that is a
/// write (insert, update, delete), a command marked <see cref="AuditedCommandExtensions.SkipReadAudit"/>, and anything inside
/// a <see cref="ReadAuditBypass"/> are not. Execute calls that fail record nothing.
/// </summary>
internal sealed class AuditedDbCommand(DbCommand inner, AuditedDbDataSource source, AuditedDbConnection? connection) : DbCommand
{
    private AuditedDbConnection? _connection = connection;

    internal ReadDeclaration? Declaration { get; set; }

    internal bool SkipAudit { get; set; }

    [AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
        set => inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => (DbConnection?)_connection ?? inner.Connection;
        set
        {
            _connection = value as AuditedDbConnection;
            inner.Connection = _connection is null ? value : _connection.Inner;
        }
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value;
    }

    public override void Cancel() => inner.Cancel();

    public override void Prepare() => inner.Prepare();

    public override Task PrepareAsync(CancellationToken cancellationToken = default) => inner.PrepareAsync(cancellationToken);

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    public override int ExecuteNonQuery() => inner.ExecuteNonQuery();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => inner.ExecuteNonQueryAsync(cancellationToken);

    public override object? ExecuteScalar()
    {
        var audit = Plan();
        var result = inner.ExecuteScalar();
        audit?.RecordScalar(result, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var audit = Plan();
        var result = await inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (audit is not null)
            await audit.RecordScalar(result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var audit = Plan();
        var reader = inner.ExecuteReader(behavior);
        return audit is null ? reader : new AuditedDbDataReader(reader, audit);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var audit = Plan();
        var reader = await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        return audit is null ? reader : new AuditedDbDataReader(reader, audit);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Decides, from the statement as it is now, whether this execution is audited. Null means it is not.</summary>
    private ReadAuditPlan? Plan()
    {
        if (SkipAudit || ReadAuditBypass.IsActive)
            return null;
        var sql = inner.CommandText ?? string.Empty;
        var kind = QueryShape.KindOf(sql);
        if (kind is "insert" or "update" or "delete")
            return null;
        if (Declaration is null && !source.Options.RecordUntaggedReads)
            return null;
        var objectType = Declaration?.ObjectType ?? source.Options.UntaggedObjectType;
        return new ReadAuditPlan(source.Auditor, objectType, Declaration?.PersonalIdColumn, kind, QueryShape.Hash(sql));
    }

    /// <summary>The audit of one execution: what to record and how, once the result is known.</summary>
    internal sealed class ReadAuditPlan(ReadAuditor auditor, string objectType, string? personalIdColumn, string kind, string shapeHash)
    {
        public string? PersonalIdColumn { get; } = personalIdColumn;

        public Task RecordScalar(object? result, CancellationToken ct)
        {
            var count = result is null or DBNull ? 0 : 1;
            return RecordBulk(count, ct);
        }

        public async Task RecordRows(long rows, IReadOnlyList<string> personalIds, CancellationToken ct)
        {
            // Holding the bypass keeps a log that reads through an audited source from recording its own reads.
            using var _ = ReadAuditBypass.Suppress();
            if (PersonalIdColumn is not null)
            {
                foreach (var id in personalIds)
                    await auditor.RecordAsync(new DataRead(objectType, 1, kind, shapeHash, id), ct).ConfigureAwait(false);
                return;
            }

            await RecordBulk(rows, ct).ConfigureAwait(false);
        }

        private async Task RecordBulk(long count, CancellationToken ct)
        {
            using var _ = ReadAuditBypass.Suppress();
            var capped = (int)Math.Min(count, int.MaxValue);
            await auditor.RecordAsync(new DataRead(objectType, capped, kind, shapeHash), ct).ConfigureAwait(false);
        }

        public static string IdText(object? value) => value switch
        {
            null or DBNull => throw new InvalidOperationException("A personal read row has no id in the declared id column."),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? throw new InvalidOperationException("A personal read row has no id in the declared id column."),
        };
    }
}
