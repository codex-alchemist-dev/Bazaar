using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Postgres;
using Bazaar.Server.OfflineSync.Rules;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres.Tests;

/// <summary>
/// A fresh database with the offline_sync component migrations applied, plus a processor wired exactly as the host wires it:
/// the real rules over the Postgres states, the Postgres store, audit sink and write scope. This project cannot reference
/// Bazaar.Server, so it applies the embedded component SQL itself, in file-name order (as the Vms tests do).
/// </summary>
internal sealed class OfflineSyncDb : IAsyncDisposable
{
    private readonly PgTestDatabase _db;

    private OfflineSyncDb(PgTestDatabase db)
    {
        _db = db;
        DataSource = db.DataSource;
        Store = new PostgresOfflineSyncStore(DataSource);
        Processor = NewProcessor(DataSource);
    }

    public NpgsqlDataSource DataSource { get; }

    public PostgresOfflineSyncStore Store { get; }

    public OfflineSyncProcessor Processor { get; }

    public static async Task<OfflineSyncDb> CreateAsync(CancellationToken ct) =>
        new(await PgTestDatabase.CreateAsync("offline_sync", MigrateAsync, ct));

    public static OfflineSyncProcessor NewProcessor(NpgsqlDataSource dataSource, params IOfflineMergeRule[] extraRules)
    {
        var review = new PostgresReviewSink(dataSource);
        var lww = new PostgresLastWriterWinsState(dataSource);
        IOfflineMergeRule[] rules =
        [
            new GcMessageRule(new PostgresGcAppendState(dataSource)),
            new GcMessageEditRule(new PostgresGcEditState(dataSource)),
            new KanbanFieldRule(new PostgresKanbanFieldState(dataSource), new AllowAllTransitions(), review),
            new KanbanEdgeRule(new PostgresKanbanEdgeState(dataSource), review),
            new ConstellationPositionRule(lww),
            new PreferenceRule(lww, new NoSafetyKeys(), review),
            new AttachmentRule(new PostgresAttachmentState(dataSource)),
            new AuditActionRule(new PostgresAuditActionSink(dataSource)),
            .. extraRules,
        ];
        return new OfflineSyncProcessor(
            rules,
            new PostgresOfflineSyncStore(dataSource),
            new PostgresOfflineMergeAudit(dataSource),
            new FixedClock(),
            new OfflineSyncOptions(),
            new PostgresOfflineWriteScopeFactory(dataSource));
    }

    public async Task<long> CountAsync(string table, string? where = null)
    {
        await using var cmd = DataSource.CreateCommand($"SELECT count(*) FROM {table}" + (where is null ? "" : " WHERE " + where));
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = OfflineSyncPostgresServiceCollectionExtensions.MigrationAssembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.offline_sync.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException("No offline_sync migrations are embedded.");
        }

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}

internal sealed class FixedClock : IOfflineClock
{
    public long NowMillis => Make.Now;
}

internal sealed class AllowAllTransitions : ITransitionChecker
{
    public ValueTask<TransitionDecision> CheckAsync(string taskId, string? fromStatus, string toStatus, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new TransitionDecision(true));
}

internal sealed class NoSafetyKeys : ISafetyKeyPolicy
{
    public bool IsSafetyRelevant(string key) => false;
}

/// <summary>Claims through a real state, then fails: the claim and the append it made must roll back with the write.</summary>
internal sealed class FailAfterAppendRule(PostgresGcAppendState state) : IOfflineMergeRule
{
    public OfflineDataClass DataClass => OfflineDataClass.GcMessage;

    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        await state.TryClaimWriteAsync(write.WriteId, cancellationToken);
        await state.AppendAsync(write.Target, write.Payload, cancellationToken);
        throw new InvalidOperationException("boom");
    }
}

internal static class Make
{
    public const long Now = 1_000_000_000_000;

    public static OfflineWrite Write(Guid device, long seq, OfflineDataClass cls, string target, ReadOnlyMemory<byte> payload, string? baseVersion = null, long wall = Now, int counter = 0, Guid? writeId = null) =>
        new(writeId ?? Guid.NewGuid(), device, seq, cls, target, baseVersion, payload, new Hlc(wall, counter, device));

    public static OfflineWriteBatch Batch(Guid device, params OfflineWrite[] writes) => new(device, writes);

    public static byte[] Message(string body) => JsonSerializer.SerializeToUtf8Bytes(new GcMessagePayload(body), RulePayloadJsonContext.Default.GcMessagePayload);

    public static byte[] Field(string field, string value) => JsonSerializer.SerializeToUtf8Bytes(new KanbanFieldPayload(field, value), RulePayloadJsonContext.Default.KanbanFieldPayload);

    public static byte[] Edge(string from, string to, bool remove = false) => JsonSerializer.SerializeToUtf8Bytes(new KanbanEdgePayload(from, to, remove), RulePayloadJsonContext.Default.KanbanEdgePayload);

    public static byte[] Preference(string value) => JsonSerializer.SerializeToUtf8Bytes(new PreferencePayload(value), RulePayloadJsonContext.Default.PreferencePayload);

    public static (byte[] Bytes, string Hash) Blob(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
