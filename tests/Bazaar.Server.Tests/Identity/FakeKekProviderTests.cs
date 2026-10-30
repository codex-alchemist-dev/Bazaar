using System.Security.Cryptography;
using Bazaar.Server.Identity.Keys;

namespace Bazaar.Server.Tests.Identity;

public sealed class FakeKekProviderTests : IDisposable
{
    private readonly FakeKekProvider _kek = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kek.Dispose();

    [Fact]
    public async Task Wrap_then_unwrap_returns_the_original_key()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = "dek:0197a000-0000-7000-8000-000000000001"u8.ToArray();

        var wrapped = await _kek.WrapAsync(dek, aad, Ct);
        var unwrapped = await _kek.UnwrapAsync(wrapped, aad, Ct);

        Assert.Equal(dek, unwrapped);
    }

    [Fact]
    public async Task Wrapping_the_same_key_twice_gives_different_bytes()
    {
        var dek = RandomNumberGenerator.GetBytes(32);

        var first = await _kek.WrapAsync(dek, ReadOnlyMemory<byte>.Empty, Ct);
        var second = await _kek.WrapAsync(dek, ReadOnlyMemory<byte>.Empty, Ct);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Unwrap_fails_when_the_associated_data_differs()
    {
        var wrapped = await _kek.WrapAsync(RandomNumberGenerator.GetBytes(32), "row-a"u8.ToArray(), Ct);

        await Assert.ThrowsAsync<KekUnwrapException>(() => _kek.UnwrapAsync(wrapped, "row-b"u8.ToArray(), Ct).AsTask());
    }

    [Fact]
    public async Task Unwrap_fails_when_a_byte_is_flipped()
    {
        var wrapped = await _kek.WrapAsync(RandomNumberGenerator.GetBytes(32), ReadOnlyMemory<byte>.Empty, Ct);
        wrapped[^1] ^= 0x01;

        await Assert.ThrowsAsync<KekUnwrapException>(() => _kek.UnwrapAsync(wrapped, ReadOnlyMemory<byte>.Empty, Ct).AsTask());
    }

    [Fact]
    public async Task Unwrap_fails_on_a_truncated_wrap()
    {
        await Assert.ThrowsAsync<KekUnwrapException>(() => _kek.UnwrapAsync(new byte[10], ReadOnlyMemory<byte>.Empty, Ct).AsTask());
    }

    [Fact]
    public async Task Unwrap_with_another_kek_fails()
    {
        using var other = new FakeKekProvider("fake-kek-2");
        var wrapped = await other.WrapAsync(RandomNumberGenerator.GetBytes(32), ReadOnlyMemory<byte>.Empty, Ct);

        await Assert.ThrowsAsync<KekUnwrapException>(() => _kek.UnwrapAsync(wrapped, ReadOnlyMemory<byte>.Empty, Ct).AsTask());
    }
}
