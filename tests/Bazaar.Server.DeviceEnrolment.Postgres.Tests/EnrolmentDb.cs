using System.Security.Cryptography;
using Bazaar.Server.Biometric;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Audit;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

/// <summary>A database with the remote namespace's first file (human, device, approval_key) and the module's own file. Nothing else is needed.</summary>
internal static class EnrolmentDb
{
    public static Task<PgTestDatabase> CreateAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("enrol", MigrateAsync, ct);

    /// <remarks>
    /// Runs the two files straight on the database. The migration runner orders non-component namespaces by name, so it would apply
    /// device_enrolment before remote; the host orders remote first because remote is a component (MigrationNamespaces.Order).
    /// </remarks>
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var remote = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly).Load("remote").Single(f => f.Version == 1);
        using var stream = typeof(PostgresEnrolmentStore).Assembly.GetManifestResourceStream(
            "Bazaar.Server.DeviceEnrolment.Postgres.Sql.components.device_enrolment.0001_device_enrolment.sql")
            ?? throw new InvalidOperationException("device_enrolment/0001 is not embedded.");
        using var reader = new StreamReader(stream);
        foreach (var sql in new[] { remote.Sql, reader.ReadToEnd() })
        {
            await using var cmd = dataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<(Guid Human, Guid Device)> InsertDeviceAsync(NpgsqlDataSource ds, string deviceClass, CancellationToken ct)
    {
        var human = Guid.NewGuid();
        var device = Guid.NewGuid();
        await Exec(ds, "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Owner', 'owner', 'active')", ct, human);
        await Exec(ds, "INSERT INTO device (id, human_id, label, class) VALUES ($1, $2, 'laptop', $3)", ct, device, human, deviceClass);
        return (human, device);
    }

    public static async Task Exec(NpgsqlDataSource ds, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long> Count(NpgsqlDataSource ds, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static byte[] Nonce() => RandomNumberGenerator.GetBytes(32);
}

/// <summary>Records audit rows; can be told to fail, to prove the commit rolls back with it.</summary>
internal sealed class RecordingAudit : ITransactionalAuditLog
{
    public List<AuditEntry> Entries { get; } = [];
    public bool Throw { get; set; }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }

    public Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default)
    {
        if (Throw)
            throw new InvalidOperationException("audit down");
        Entries.Add(e);
        return Task.CompletedTask;
    }
}
