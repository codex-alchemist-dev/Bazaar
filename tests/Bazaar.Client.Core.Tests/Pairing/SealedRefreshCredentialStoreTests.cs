using System.Text;
using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Core.Tests.Pairing;

public sealed class SealedRefreshCredentialStoreTests
{
    [Fact]
    public async Task LoadReturnsNullWhenNothingIsStored()
    {
        var store = new SealedRefreshCredentialStore(new FakeSealer(), new MemoryBlobStore());

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTheCredential()
    {
        var store = new SealedRefreshCredentialStore(new FakeSealer(), new MemoryBlobStore());

        await store.SaveAsync("refresh-abc", TestContext.Current.CancellationToken);

        Assert.Equal("refresh-abc", await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheBlobStoreNeverHoldsThePlaintext()
    {
        var blobs = new MemoryBlobStore();
        var store = new SealedRefreshCredentialStore(new FakeSealer(), blobs);

        await store.SaveAsync("refresh-abc", TestContext.Current.CancellationToken);

        Assert.NotNull(blobs.Blob);
        Assert.DoesNotContain("refresh-abc", Encoding.UTF8.GetString(blobs.Blob));
    }

    [Fact]
    public async Task SaveReplacesThePreviousCredential()
    {
        var store = new SealedRefreshCredentialStore(new FakeSealer(), new MemoryBlobStore());

        await store.SaveAsync("first", TestContext.Current.CancellationToken);
        await store.SaveAsync("second", TestContext.Current.CancellationToken);

        Assert.Equal("second", await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearRemovesTheCredential()
    {
        var blobs = new MemoryBlobStore();
        var store = new SealedRefreshCredentialStore(new FakeSealer(), blobs);
        await store.SaveAsync("refresh-abc", TestContext.Current.CancellationToken);

        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Null(blobs.Blob);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnopenableBlobReadsAsAbsentAndIsDeleted()
    {
        // A lost Keystore key (device reset) makes the sealed bytes unopenable: the device must pair again.
        var sealer = new FakeSealer();
        var blobs = new MemoryBlobStore();
        var store = new SealedRefreshCredentialStore(sealer, blobs);
        await store.SaveAsync("refresh-abc", TestContext.Current.CancellationToken);

        sealer.KeyLost = true;

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Null(blobs.Blob);
    }

    [Fact]
    public async Task SaveRejectsABlankCredential()
    {
        var store = new SealedRefreshCredentialStore(new FakeSealer(), new MemoryBlobStore());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("   ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new SealedRefreshCredentialStore(null!, new MemoryBlobStore()));
        Assert.Throws<ArgumentNullException>(() => new SealedRefreshCredentialStore(new FakeSealer(), null!));
    }

    /// <summary>Reversible XOR behind a marker prefix. <see cref="KeyLost"/> makes every open fail, as a Keystore loss would.</summary>
    private sealed class FakeSealer : ICredentialSealer
    {
        private const byte Mask = 0x5A;
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("SEAL");

        public bool KeyLost { get; set; }

        public byte[] Seal(byte[] plaintext)
        {
            var sealedBytes = new byte[Marker.Length + plaintext.Length];
            Marker.CopyTo(sealedBytes, 0);
            for (var i = 0; i < plaintext.Length; i++)
            {
                sealedBytes[Marker.Length + i] = (byte)(plaintext[i] ^ Mask);
            }

            return sealedBytes;
        }

        public byte[]? Open(byte[] sealedBytes)
        {
            if (KeyLost || sealedBytes.Length < Marker.Length || !sealedBytes.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            {
                return null;
            }

            var plaintext = new byte[sealedBytes.Length - Marker.Length];
            for (var i = 0; i < plaintext.Length; i++)
            {
                plaintext[i] = (byte)(sealedBytes[Marker.Length + i] ^ Mask);
            }

            return plaintext;
        }
    }

    private sealed class MemoryBlobStore : ISealedBlobStore
    {
        public byte[]? Blob { get; private set; }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Blob);

        public Task WriteAsync(byte[] blob, CancellationToken cancellationToken = default)
        {
            Blob = blob;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Blob = null;
            return Task.CompletedTask;
        }
    }
}
