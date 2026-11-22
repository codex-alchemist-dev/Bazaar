using System.Text;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Calendar.Tests;

/// <summary>
/// A cloned test database with every common migration applied (0001 GC core, 0010 agents, 0017 calendar), plus helpers to
/// create agents and GCs and to read raw rows.
/// </summary>
public sealed class CalendarDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("CalendarDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(MigrationSource(), "linux", CancellationToken.None);
    }

    /// <summary>The server's files plus the calendar component's own namespace (the visibility column), as the host composes them.</summary>
    public static IMigrationSource MigrationSource()
    {
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);
        return new InMemoryMigrationSource(source.Namespaces().SelectMany(source.Load).Concat(CalendarComponentFiles()));
    }

    /// <summary>
    /// The files under <c>Sql/components/calendar</c> of the calendar module, read from its embedded resources. The host does the same
    /// through its component source; the server's own source only knows the <c>Sql/&lt;namespace&gt;</c> layout.
    /// </summary>
    private static IEnumerable<MigrationFile> CalendarComponentFiles()
    {
        var assembly = typeof(CalendarComponent).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = System.Text.RegularExpressions.Regex.Match(resource, @"\.Sql\.components\.calendar\.(?<file>[^.]+\.sql)$");
            if (!match.Success)
                continue;
            if (!MigrationFile.TryParseFileName(match.Groups["file"].Value, out var version, out var name))
                throw new InvalidOperationException($"Migration resource '{resource}' does not follow NNNN_name.sql.");
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            yield return new MigrationFile("calendar", version, name, reader.ReadToEnd());
        }
    }

    public async Task<Guid> CreateAgentAsync(string name = "agent-a")
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, $2, 'active')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<Guid> CreateGcAsync()
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'calendar', $1)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Runs a statement with positional parameters and returns the first column of the first row.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object?[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a statement with positional parameters.</summary>
    public async Task ExecAsync(string sql, params object?[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The GC system lines written for one calendar event, in seq order: (change, version, client_msg_id).</summary>
    public async Task<List<(string Change, long Version, string ClientMsgId)>> GcLinesAsync(Guid gcId, Guid eventId)
    {
        await using var cmd = DataSource.CreateCommand(
            """
            SELECT payload->>'change', (payload->>'version')::bigint, client_msg_id
            FROM gc_event
            WHERE gc_id = $1 AND kind = 'system' AND payload->>'event_id' = $2
            ORDER BY seq
            """);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = eventId.ToString() });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, long, string)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        return rows;
    }

    /// <summary>A GC log writer over this database with a fake clock and a no-op notifier.</summary>
    public GcLogWriter GcWriter(TimeProvider time) => new(DataSource, new NullNotifier(), time);

    /// <summary>Plaintext codec for tests that do not care about sealing.</summary>
    public static IGcBodyCodec PlainCodec() => new PlaintextGcBodyCodec();

    private sealed class NullNotifier : IGcNotifier
    {
        public void Notify(Guid gcId, long seq)
        {
        }
    }
}

/// <summary>
/// A codec that is not plaintext: it reverses the bytes under a fixed key id. Tests use it to prove the title column holds a
/// sealed value and that the service opens it again.
/// </summary>
public sealed class ReversingBodyCodec : IGcBodyCodec
{
    public static readonly Guid KeyId = Guid.Parse("00000000-0000-7000-8000-00000000c0de");

    public ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Array.Reverse(bytes);
        return ValueTask.FromResult(new MessageBody(bytes, KeyId));
    }

    public ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct)
    {
        if (keyId != KeyId)
            throw new InvalidOperationException("Unknown key.");
        var bytes = ciphertext.ToArray();
        Array.Reverse(bytes);
        return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(bytes));
    }
}
