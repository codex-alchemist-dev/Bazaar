using System.Security.Cryptography;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Mirror.Tests;

public class PostgresMirrorStoreTests
{
    /// <summary>Runs the embedded mirror/0001 file. Mirror has no foreign keys, so it needs no other migration.</summary>
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var stream = typeof(MirrorComponent).Assembly.GetManifestResourceStream("Bazaar.Server.Mirror.Sql.components.mirror.0001_mirror.sql")
            ?? throw new InvalidOperationException("mirror/0001 is not embedded.");
        using var reader = new StreamReader(stream);
        await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static AesGcmMirrorCipher NewCipher() => new(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));

    private static MirrorSignal Sig(string topic, int n) =>
        new(Guid.NewGuid(), MirrorSignalKind.CardRejected, topic, MirrorDirection.Avoid, $"card-{n}", 1, "too chatty for my taste", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Cipher_round_trips_and_binds_to_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var cipher = NewCipher();
        var sealedValue = await cipher.SealAsync("secret", [1, 2], ct);

        Assert.Equal("secret", await cipher.OpenAsync(sealedValue, [1, 2], ct));
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(async () => await cipher.OpenAsync(sealedValue, [9], ct));
        await Assert.ThrowsAsync<CryptographicException>(async () => await NewCipher().OpenAsync(sealedValue, [1, 2], ct));
    }

    [Fact]
    public async Task Service_over_postgres_learns_pauses_deletes_and_wipes_with_sealed_rows()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mirror", MigrateAsync, ct);
        var service = new MirrorService(new PostgresMirrorStore(db.DataSource, NewCipher()), new MirrorOptions(), TimeProvider.System);

        for (var i = 0; i < 5; i++) { Assert.True(await service.RecordAsync(Sig("tone:chatty", i), ct)); }

        // The payload is sealed at rest.
        await using (var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM mirror_signal WHERE position('\\x636861747479'::bytea in ciphertext) > 0"))
        {
            Assert.Equal(0L, await cmd.ExecuteScalarAsync(ct));
        }

        var profile = (await service.GetProfileAsync(ct))!;
        Assert.Equal(5, profile.Version);
        Assert.Equal(5, profile.Rules.Single().Count);

        await service.SetPausedAsync(true, ct);
        Assert.False(await service.RecordAsync(Sig("tone:chatty", 9), ct));
        await service.SetPausedAsync(false, ct);

        await service.DeleteRuleAsync("tone:chatty", MirrorDirection.Avoid, ct);
        Assert.Empty((await service.GetProfileAsync(ct))!.Rules);

        await service.WipeAsync(ct);
        Assert.Null(await service.GetProfileAsync(ct));
        await using var count = db.DataSource.CreateCommand("SELECT (SELECT count(*) FROM mirror_signal) + (SELECT count(*) FROM mirror_profile) + (SELECT count(*) FROM mirror_suppressed)");
        Assert.Equal(0L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Destroying_the_key_makes_stored_signals_unreadable()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mirror", MigrateAsync, ct);
        await new PostgresMirrorStore(db.DataSource, NewCipher()).AddSignalAsync(Sig("t", 1), ct);

        // A new random key stands in for the destroyed one.
        await Assert.ThrowsAsync<CryptographicException>(() => new PostgresMirrorStore(db.DataSource, NewCipher()).ListSignalsAsync(ct));
    }
}
