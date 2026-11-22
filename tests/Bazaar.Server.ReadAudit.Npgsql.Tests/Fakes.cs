using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.ReadAudit.Npgsql.Tests;

internal sealed class RecordingLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Func<Task>? OnAppend { get; set; }

    public async Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        if (OnAppend is not null)
            await OnAppend().ConfigureAwait(false);
    }

    public IReadOnlyList<JsonObject> Payloads() => Entries.Select(e => JsonNode.Parse(e.Payload)!.AsObject()).ToList();
}

internal sealed class ThrowingLog : IAuditLog
{
    public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new InvalidOperationException("audit store down");
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Inner source with canned results. Records the statements that reached it.</summary>
internal sealed class FakeDataSource : DbDataSource
{
    public DataTable Rows { get; set; } = new();

    public object? Scalar { get; set; }

    public List<string> Executed { get; } = [];

    public override string ConnectionString => "fake";

    protected override DbConnection CreateDbConnection() => new FakeConnection(this);

    protected override DbCommand CreateDbCommand(string? commandText = null) => new FakeCommand(this) { CommandText = commandText! };

    public static DataTable Table(params (int Id, string Name)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("id", typeof(int));
        t.Columns.Add("name", typeof(string));
        foreach (var (id, name) in rows)
            t.Rows.Add(id, name);
        return t;
    }
}

internal sealed class FakeConnection(FakeDataSource source) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "fake";

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeCommand(source) { Connection = this };
}

internal sealed class FakeCommand(FakeDataSource source) : DbCommand
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameters();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }

    public override int ExecuteNonQuery()
    {
        source.Executed.Add(CommandText);
        return 1;
    }

    public override object? ExecuteScalar()
    {
        source.Executed.Add(CommandText);
        return source.Scalar;
    }

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        source.Executed.Add(CommandText);
        return source.Rows.CreateDataReader();
    }
}

internal sealed class FakeParameters : DbParameterCollection
{
    public override int Count => 0;

    public override object SyncRoot => this;

    public override int Add(object value) => throw new NotSupportedException();

    public override void AddRange(Array values) { }

    public override void Clear() { }

    public override bool Contains(object value) => false;

    public override bool Contains(string value) => false;

    public override void CopyTo(Array array, int index) { }

    public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

    public override int IndexOf(object value) => -1;

    public override int IndexOf(string parameterName) => -1;

    public override void Insert(int index, object value) { }

    public override void Remove(object value) { }

    public override void RemoveAt(int index) { }

    public override void RemoveAt(string parameterName) { }

    protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

    protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

    protected override void SetParameter(int index, DbParameter value) { }

    protected override void SetParameter(string parameterName, DbParameter value) { }
}
