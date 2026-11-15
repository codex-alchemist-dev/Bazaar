using Bazaar.Testing.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Tests.Remote;

/// <summary>Applies remote/0001 to a clean database and checks the constraints doc 32 relies on.</summary>
public sealed class RemotePairingSchemaTests : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await using var cmd = DataSource.CreateCommand(ReadMigration());
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string ReadMigration()
    {
        var assembly = typeof(Bazaar.Server.Data.Migrations.MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream("Bazaar.Server.Data.Sql.remote.0001_pairing_auth.sql")
            ?? throw new InvalidOperationException("remote/0001_pairing_auth.sql is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task SecondOwnerIsRejected()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL on this host.");
        await InsertHumanAsync("owner");
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertHumanAsync("owner"));
        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task OnlyOneLiveRefreshCredentialPerDevice()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL on this host.");
        var device = await InsertDeviceAsync();
        await InsertRefreshAsync(device, 1);
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertRefreshAsync(device, 2));
        Assert.Equal("23505", error.SqlState);

        await using (var retire = DataSource.CreateCommand("UPDATE refresh_credential SET retired_at = now() WHERE device_id = $1"))
        {
            retire.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = device });
            await retire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await InsertRefreshAsync(device, 3);
    }

    [Fact]
    public async Task UserCodeHashMustBe32Bytes()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL on this host.");
        var owner = await InsertHumanAsync("coworker");
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO pairing_offer (id, created_by, role, user_code_hash, expires_at) VALUES ($1, $2, 'coworker', $3, now() + interval '10 minutes')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = owner });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = new byte[31], NpgsqlDbType = NpgsqlDbType.Bytea });
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("23514", error.SqlState);
    }

    private async Task<Guid> InsertHumanAsync(string role)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'test', $2, 'active')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> InsertDeviceAsync()
    {
        var human = await InsertHumanAsync("coworker");
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO device (id, human_id, label, class) VALUES ($1, $2, 'laptop', 'windows')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = human });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task InsertRefreshAsync(Guid device, byte marker)
    {
        var hash = new byte[32];
        hash[0] = marker;
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO refresh_credential (id, device_id, hash, expires_at) VALUES ($1, $2, $3, now() + interval '30 days')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = device });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash, NpgsqlDbType = NpgsqlDbType.Bytea });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
