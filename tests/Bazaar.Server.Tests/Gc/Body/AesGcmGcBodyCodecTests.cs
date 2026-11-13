using System.Text;
using Bazaar.Server.Components.Erasure;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Tests.Identity;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Body;

/// <summary>Database-free checks of the codec's fail-closed and legacy paths.</summary>
public sealed class AesGcmGcBodyCodecFailClosedTests
{
    // NpgsqlDataSource.Create does not connect, so these tests need no database: each path below returns or throws before any query.
    private static NpgsqlDataSource Unconnected() => NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");

    [Fact]
    public async Task Sealing_without_a_kek_provider_fails_closed()
    {
        using var ds = Unconnected();
        var codec = new AesGcmGcBodyCodec(ds, kek: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => codec.SealAsync(Guid.CreateVersion7(), "must not be stored", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("No key-encryption key provider", error.Message);
    }

    [Fact]
    public async Task Sealing_for_an_empty_author_is_refused()
    {
        using var ds = Unconnected();
        var codec = new AesGcmGcBodyCodec(ds, new FakeKekProvider());

        await Assert.ThrowsAsync<ArgumentException>(
            () => codec.SealAsync(Guid.Empty, "no author", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Rows_from_the_interim_codec_still_open_as_plaintext()
    {
        using var ds = Unconnected();
        var codec = new AesGcmGcBodyCodec(ds, kek: null);
        var stored = Encoding.UTF8.GetBytes("written before per-person keys");

        var text = await codec.OpenAsync(stored, PlaintextGcBodyCodec.PlaintextKeyId, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal("written before per-person keys", text);
    }
}

/// <summary>Round trips, per-person keys and crypto-shredding on the real database (docs/design/04, Question 2).</summary>
public sealed class AesGcmGcBodyCodecTests(GcDb db) : IClassFixture<GcDb>
{
    private AesGcmGcBodyCodec CreateCodec(FakeKekProvider kek) => new(db.DataSource, kek);

    [Fact]
    public async Task A_sealed_body_opens_to_the_same_text_and_is_not_stored_as_plaintext()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        using var kek = new FakeKekProvider();
        var codec = CreateCodec(kek);
        var author = Guid.CreateVersion7();

        var body = await codec.SealAsync(author, "a private note", ct);

        Assert.NotEqual(PlaintextGcBodyCodec.PlaintextKeyId, body.KeyId);
        Assert.Equal(12, body.Nonce.Length);
        Assert.True(body.BodyCiphertext.Span.IndexOf(Encoding.UTF8.GetBytes("a private note")) < 0);
        Assert.Equal("a private note", await codec.OpenAsync(body.BodyCiphertext, body.KeyId, body.Nonce, ct));
    }

    [Fact]
    public async Task One_author_reuses_one_key_and_two_authors_get_different_keys()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        using var kek = new FakeKekProvider();
        var codec = CreateCodec(kek);
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();

        var first = await codec.SealAsync(alice, "one", ct);
        var second = await codec.SealAsync(alice, "two", ct);
        var other = await codec.SealAsync(bob, "three", ct);

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.NotEqual(first.KeyId, other.KeyId);
        Assert.NotEqual(first.Nonce.ToArray(), second.Nonce.ToArray());
    }

    [Fact]
    public async Task Erasing_the_author_makes_every_body_unreadable_and_keeps_the_rows()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        using var kek = new FakeKekProvider();
        var codec = CreateCodec(kek);
        var author = Guid.CreateVersion7();
        var body = await codec.SealAsync(author, "to be shredded", ct);

        var report = await new PostgresPersonKeyEraser(db.DataSource).EraseAsync(author, ct);

        Assert.Equal(1, report.Destroyed);
        Assert.Null(await codec.OpenAsync(body.BodyCiphertext, body.KeyId, body.Nonce, ct));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM person_key WHERE key_id = $1 AND destroyed_at IS NOT NULL", body.KeyId));
    }
}
