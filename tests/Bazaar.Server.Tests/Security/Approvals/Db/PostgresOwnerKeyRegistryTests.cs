using Bazaar.Approvals;
using Bazaar.Approvals.Db;
using Bazaar.Server.Security.Approvals;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Approvals.Db;

/// <summary>The owner key registry over approval_key (doc 32 section 5a), run against migrated PostgreSQL.</summary>
public sealed class PostgresOwnerKeyRegistryTests
{
    private static readonly Guid _human = Guid.Parse("0196a6f0-0000-7000-8000-000000000501");
    private static readonly Guid _device = Guid.Parse("0196a6f0-0000-7000-8000-000000000502");
    private static readonly byte[] _spki = Enumerable.Range(0, 91).Select(i => (byte)(i + 1)).ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task SeedAsync(ApprovalTestDb db, bool keyRevoked, bool deviceRevoked)
    {
        await using (var human = db.DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Owner', 'owner', 'active')"))
        {
            human.Parameters.AddWithValue(_human);
            await human.ExecuteNonQueryAsync(Ct);
        }

        await using (var device = db.DataSource.CreateCommand(
            "INSERT INTO device (id, human_id, label, class, revoked_at) VALUES ($1, $2, 'Laptop', 'windows', $3)"))
        {
            device.Parameters.AddWithValue(_device);
            device.Parameters.AddWithValue(_human);
            device.Parameters.AddWithValue(deviceRevoked ? (object)DateTimeOffset.UtcNow : DBNull.Value);
            await device.ExecuteNonQueryAsync(Ct);
        }

        await using var key = db.DataSource.CreateCommand(
            "INSERT INTO approval_key (id, human_id, device_id, public_key, revoked_at) VALUES ($1, $2, $3, $4, $5)");
        key.Parameters.AddWithValue(Guid.Parse("0196a6f0-0000-7000-8000-000000000503"));
        key.Parameters.AddWithValue(_human);
        key.Parameters.AddWithValue(_device);
        key.Parameters.AddWithValue(_spki);
        key.Parameters.AddWithValue(keyRevoked ? (object)DateTimeOffset.UtcNow : DBNull.Value);
        await key.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task An_active_device_key_is_found_for_its_owner()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        await SeedAsync(db, keyRevoked: false, deviceRevoked: false);

        var key = await new PostgresOwnerKeyRegistry(db.DataSource).FindAsync(_device.ToString("D"), Ct);

        Assert.NotNull(key);
        Assert.Equal(_human.ToString("D"), key!.OwnerId);
        Assert.Equal(_device.ToString("D"), key.DeviceId);
        Assert.Equal(_spki, key.PublicKeySpki);
        Assert.False(key.Revoked);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_revoked_key_or_a_revoked_device_is_reported_as_revoked(bool keyRevoked, bool deviceRevoked)
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        await SeedAsync(db, keyRevoked, deviceRevoked);

        var key = await new PostgresOwnerKeyRegistry(db.DataSource).FindAsync(_device.ToString("D"), Ct);

        Assert.True(key!.Revoked);
    }

    [Fact]
    public async Task An_unknown_or_malformed_device_id_finds_nothing()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var registry = new PostgresOwnerKeyRegistry(db.DataSource);

        Assert.Null(await registry.FindAsync(_device.ToString("D"), Ct));
        Assert.Null(await registry.FindAsync("not-a-guid", Ct));
    }
}
