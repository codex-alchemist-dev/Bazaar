using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Mirror.Tests;

public class PersonKeyMirrorCipherTests
{
    /// <summary>A fake KEK that wraps with AES-GCM and binds the associated data, like the real one.</summary>
    private sealed class FakeKek : IKekProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public string KekId => "fake";

        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[12 + dek.Length + 16];
            nonce.CopyTo(output, 0);
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, dek.Span, output.AsSpan(12, dek.Length), output.AsSpan(12 + dek.Length, 16), associatedData.Span);
            return ValueTask.FromResult(output);
        }

        public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            var span = wrappedDek.Span;
            var len = span.Length - 28;
            var plain = new byte[len];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(span[..12], span.Slice(12, len), span.Slice(12 + len, 16), plain, associatedData.Span);
            return ValueTask.FromResult(plain);
        }
    }

    /// <summary>The two tables the cipher reads, reduced to their columns (the full ones need PostgreSQL 18 and other migrations).</summary>
    private static async Task MigrateAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            CREATE TABLE human (id uuid PRIMARY KEY, role text NOT NULL, status text NOT NULL);
            CREATE TABLE person_key (
                key_id uuid PRIMARY KEY, person_id uuid NOT NULL, wrapped_key bytea,
                created_at timestamptz NOT NULL DEFAULT now(), destroyed_at timestamptz);
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddOwnerAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO human (id, role, status) VALUES ($1, 'owner', 'active')");
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Seals_under_one_owner_key_and_erasure_makes_values_unreadable()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mirrorkey", MigrateAsync, ct);
        await AddOwnerAsync(db.DataSource, ct);
        var cipher = new PersonKeyMirrorCipher(db.DataSource, new FakeKek());

        var a = await cipher.SealAsync("one", [1], ct);
        var b = await cipher.SealAsync("two", [2], ct);
        Assert.Equal(a.KeyId, b.KeyId);
        Assert.Equal("one", await cipher.OpenAsync(a, [1], ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await cipher.OpenAsync(a, [2], ct));

        // Erasure clears the wrapped key (doc 04); the next read fails at once.
        await using (var erase = db.DataSource.CreateCommand("UPDATE person_key SET wrapped_key = NULL, destroyed_at = now()"))
        {
            await erase.ExecuteNonQueryAsync(ct);
        }

        await Assert.ThrowsAsync<CryptographicException>(async () => await cipher.OpenAsync(a, [1], ct));
    }

    [Fact]
    public async Task Fails_closed_without_an_owner()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mirrorkey", MigrateAsync, ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new PersonKeyMirrorCipher(db.DataSource, new FakeKek()).SealAsync("x", [1], ct));
    }

    [Fact]
    public async Task Fails_closed_without_a_kek_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ds = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var services = new ServiceCollection().AddSingleton(ds);
        new MirrorComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.GetRequiredService<IMirrorCipher>().SealAsync("x", [1], ct));
        Assert.IsType<PostgresMirrorStore>(provider.GetRequiredService<IMirrorStore>());
    }
}
