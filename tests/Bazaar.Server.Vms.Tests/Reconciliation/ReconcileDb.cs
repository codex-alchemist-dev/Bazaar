using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.Reconciliation;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Vms.Tests.Reconciliation;

/// <summary>
/// A fresh cloned database with the <c>vms</c> component migrations and the <c>audit_log</c> table from common migration 0004.
/// Each test class instance gets its own database, so a reconcile run sees only the rows that test inserted.
/// This project cannot reference Bazaar.Server, so the audit DDL is repeated here; keep it in step with 0004.
/// </summary>
public class ReconcileDb : PgFixture, IAsyncLifetime
{
    public static readonly DateTimeOffset Epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] _genesisHash = new byte[SHA256.HashSizeInBytes];

    private const string _auditDdl = """
        CREATE TABLE audit_log (
            seq       bigint      PRIMARY KEY CHECK (seq >= 1),
            ts        timestamptz NOT NULL,
            actor     text        NOT NULL,
            kind      text        NOT NULL,
            payload   jsonb       NOT NULL,
            prev_hash bytea       NOT NULL CHECK (octet_length(prev_hash) = 32),
            hash      bytea       NOT NULL CHECK (octet_length(hash) = 32)
        );

        REVOKE ALL ON audit_log FROM PUBLIC;
        REVOKE ALL ON audit_log FROM bazaar_app;
        GRANT INSERT, SELECT ON audit_log TO bazaar_app;

        CREATE FUNCTION audit_log_refuse_change() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'audit_log is append-only: % refused', TG_OP
                USING ERRCODE = 'insufficient_privilege';
        END
        $$;

        CREATE TRIGGER audit_log_no_update_or_delete
            BEFORE UPDATE OR DELETE ON audit_log
            FOR EACH ROW EXECUTE FUNCTION audit_log_refuse_change();
        CREATE TRIGGER audit_log_no_truncate
            BEFORE TRUNCATE ON audit_log
            FOR EACH STATEMENT EXECUTE FUNCTION audit_log_refuse_change();
        """;

    /// <summary>The repository under test, bound to this database.</summary>
    public EnvironmentRepository Repository => new(DataSource);

    /// <summary>The Postgres audit writer under test, bound to this database.</summary>
    public PgAuditWriter AuditWriter => new(DataSource);

    public override async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database; each test skips itself (see Assert.SkipWhen in the tests).
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await PgRoles.EnsureNoLoginRoleAsync(DataSource, "bazaar_app", TestContext.Current.CancellationToken);
        await ApplyComponentMigrationsAsync();
        await ExecuteScriptAsync(_auditDdl);
    }

    public async Task<GoldenImageRecord> InsertImageAsync()
    {
        var image = new GoldenImageRecord(
            Guid.CreateVersion7(), VmOs.Linux, 1, new string('a', 64), "1.0.0", GoldenImageState.Ready, Epoch);
        await Repository.InsertGoldenImageAsync(image, TestContext.Current.CancellationToken);
        return image;
    }

    private GoldenImageRecord? _image;

    /// <summary>Inserts a VM with a backend ref, unless <paramref name="backendRef"/> is null.</summary>
    public async Task<EnvironmentVmRecord> InsertVmAsync(VmState state, string? backendRef = "fake-vm", Guid? id = null)
    {
        // One golden image per database: (os, version) is unique, so every VM here shares it.
        _image ??= await InsertImageAsync();
        var image = _image;
        var vm = new EnvironmentVmRecord(
            id ?? Guid.CreateVersion7(), Guid.CreateVersion7(), VmOs.Linux, image.Id, backendRef, state, 2, 2048, 30, Epoch, Epoch);
        await Repository.InsertVmAsync(vm, TestContext.Current.CancellationToken);
        return vm;
    }

    /// <summary>Every audit row in seq order, read as the verifier reads them.</summary>
    public async Task<IReadOnlyList<AuditRow>> ReadAuditAsync()
    {
        await using var cmd = DataSource.CreateCommand(
            "SELECT seq, ts, actor, kind, payload::text, prev_hash, hash FROM audit_log ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(new AuditRow(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetFieldValue<byte[]>(6)));
        return rows;
    }

    /// <summary>The state the repository holds for a VM, read directly so the test does not depend on the reconciler.</summary>
    public async Task<VmState> StateOfAsync(Guid vmId) =>
        (await Repository.GetVmAsync(vmId, TestContext.Current.CancellationToken))!.State;

    /// <summary>Recomputes a row's hash with the <c>AuditChain</c> algorithm and checks that the chain links hold.</summary>
    public static bool ChainIsValid(IReadOnlyList<AuditRow> rows)
    {
        var prev = _genesisHash;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Seq != i + 1 || !row.PrevHash.AsSpan().SequenceEqual(prev))
                return false;
            if (!row.Hash.AsSpan().SequenceEqual(RecomputeHash(row)))
                return false;
            prev = row.Hash;
        }
        return true;
    }

    private static byte[] RecomputeHash(AuditRow row)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(number, row.Seq);
        stream.Write(number);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(number, row.Ts.UtcTicks);
        stream.Write(number);
        WriteField(stream, Encoding.UTF8.GetBytes(row.Actor));
        WriteField(stream, Encoding.UTF8.GetBytes(row.Kind));
        WriteField(stream, Encoding.UTF8.GetBytes(row.Payload));
        WriteField(stream, row.PrevHash);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private async Task ApplyComponentMigrationsAsync()
    {
        var assembly = typeof(EnvironmentRepository).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.vms.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No vms migrations are embedded in Bazaar.Server.Vms.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            await ExecuteScriptAsync(await reader.ReadToEndAsync());
        }
    }

    private async Task ExecuteScriptAsync(string sql)
    {
        await using var conn = await DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>One row of <c>audit_log</c>, as read back.</summary>
public sealed record AuditRow(long Seq, DateTimeOffset Ts, string Actor, string Kind, string Payload, byte[] PrevHash, byte[] Hash);
