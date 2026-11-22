using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>
/// Passes every call to the inner reader and counts the rows read. When the reader closes it records the read once, so the
/// count is final. For a personal declaration it collects each row's id (never other content) and records one row per id.
/// A failure to record propagates from the close or dispose.
/// </summary>
internal sealed class AuditedDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly AuditedDbCommand.ReadAuditPlan _plan;
    private readonly List<string> _ids = [];
    private long _rows;
    private int _idOrdinal = -1;
    private bool _recorded;

    internal AuditedDbDataReader(DbDataReader inner, AuditedDbCommand.ReadAuditPlan plan)
    {
        _inner = inner;
        _plan = plan;
    }

    public override int Depth => _inner.Depth;

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override int VisibleFieldCount => _inner.VisibleFieldCount;

    public override object this[int ordinal] => _inner[ordinal];

    public override object this[string name] => _inner[name];

    public override bool Read() => Counted(_inner.Read());

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        Counted(await _inner.ReadAsync(cancellationToken).ConfigureAwait(false));

    public override bool NextResult() => _inner.NextResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);

    public override void Close()
    {
        _inner.Close();
        RecordOnceAsync().GetAwaiter().GetResult();
    }

    public override async Task CloseAsync()
    {
        await _inner.CloseAsync().ConfigureAwait(false);
        await RecordOnceAsync().ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await RecordOnceAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            RecordOnceAsync().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private bool Counted(bool hadRow)
    {
        if (!hadRow)
            return false;
        _rows++;
        if (_plan.PersonalIdColumn is not null)
        {
            if (_idOrdinal < 0)
                _idOrdinal = _inner.GetOrdinal(_plan.PersonalIdColumn);
            _ids.Add(AuditedDbCommand.ReadAuditPlan.IdText(_inner.GetValue(_idOrdinal)));
        }
        return true;
    }

    private Task RecordOnceAsync()
    {
        if (_recorded)
            return Task.CompletedTask;
        _recorded = true;
        return _plan.RecordRows(_rows, _ids, CancellationToken.None);
    }

    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override Stream GetStream(int ordinal) => _inner.GetStream(ordinal);

    public override string GetString(int ordinal) => _inner.GetString(ordinal);

    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

    public override int GetValues(object[] values) => _inner.GetValues(values);

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);

    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();

    public override Task<DataTable?> GetSchemaTableAsync(CancellationToken cancellationToken = default) => _inner.GetSchemaTableAsync(cancellationToken);

    public override IEnumerator GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();
}
