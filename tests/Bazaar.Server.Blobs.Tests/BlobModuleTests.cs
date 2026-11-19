using System.Security.Cryptography;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Blobs.Tests;

public sealed class BlobModuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Build(IGcReadAccess? access = null, IBlobGcLinks? links = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _root, ["Bazaar:Http:MaxActiveUploadsPerPrincipal"] = "1" })
            .Build();
        var services = new ServiceCollection();
        if (access is not null) services.AddSingleton(access);
        if (links is not null) services.AddSingleton(links);
        services.AddBazaarBlobs(config);
        return services.BuildServiceProvider();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<string> UploadAsync(IBlobStore store, Guid owner, byte[] bytes)
    {
        var session = await store.CreateUploadAsync(owner, bytes.Length, Sha(bytes), TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        var result = await store.AppendAsync(owner, session.Id, 0, new MemoryStream(bytes), bytes.Length, TestContext.Current.CancellationToken);
        Assert.Equal(AppendStatus.Completed, result.Status);
        Assert.Equal(Sha(bytes), result.Hash);
        return result.Hash!;
    }

    [Fact]
    public async Task Uploaded_blob_round_trips_and_names_its_uploader()
    {
        await using var sp = Build();
        var store = sp.GetRequiredService<IBlobStore>();
        var hash = await UploadAsync(store, _alice, [1, 2, 3, 4]);

        await using var handle = (await store.OpenAsync(hash, TestContext.Current.CancellationToken))!.Content;
        using var copy = new MemoryStream();
        await handle.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, copy.ToArray());
        Assert.True(await store.IsUploaderAsync(hash, _alice, TestContext.Current.CancellationToken));
        Assert.False(await store.IsUploaderAsync(hash, _bob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_hash_mismatch_discards_the_upload()
    {
        await using var sp = Build();
        var store = sp.GetRequiredService<IBlobStore>();
        var session = await store.CreateUploadAsync(_alice, 3, new string('a', 64), TestContext.Current.CancellationToken);
        var result = await store.AppendAsync(_alice, session!.Id, 0, new MemoryStream([1, 2, 3]), 3, TestContext.Current.CancellationToken);
        Assert.Equal(AppendStatus.HashMismatch, result.Status);
    }

    [Fact]
    public async Task Active_upload_limit_comes_from_configuration()
    {
        await using var sp = Build();
        var store = sp.GetRequiredService<IBlobStore>();
        Assert.NotNull(await store.CreateUploadAsync(_alice, 10, null, TestContext.Current.CancellationToken));
        Assert.Null(await store.CreateUploadAsync(_alice, 10, null, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.CreateUploadAsync(_bob, 10, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_gc_access_only_the_uploader_reads()
    {
        await using var sp = Build();
        var store = sp.GetRequiredService<IBlobStore>();
        var hash = await UploadAsync(store, _alice, [9, 9]);
        var access = sp.GetRequiredService<IBlobReadAccess>();
        Assert.True(await access.CanReadAsync(hash, new GcPrincipal(_alice, PrincipalKind.Human, "alice"), TestContext.Current.CancellationToken));
        Assert.False(await access.CanReadAsync(hash, new GcPrincipal(_bob, PrincipalKind.Human, "bob"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_viewer_of_a_referencing_entry_reads_up_to_the_grant()
    {
        var gc = Guid.NewGuid();
        await using var sp = Build(new Grant(gc, _bob, 5), new Links(new BlobGcLink(gc, 3)));
        var store = sp.GetRequiredService<IBlobStore>();
        var hash = await UploadAsync(store, _alice, [7]);
        var access = sp.GetRequiredService<IBlobReadAccess>();
        Assert.True(await access.CanReadAsync(hash, new GcPrincipal(_bob, PrincipalKind.Human, "bob"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_entry_past_the_grant_stays_hidden()
    {
        var gc = Guid.NewGuid();
        await using var sp = Build(new Grant(gc, _bob, 5), new Links(new BlobGcLink(gc, 6)));
        var store = sp.GetRequiredService<IBlobStore>();
        var hash = await UploadAsync(store, _alice, [7]);
        Assert.False(await sp.GetRequiredService<IBlobReadAccess>().CanReadAsync(hash, new GcPrincipal(_bob, PrincipalKind.Human, "bob"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Blob_ids_must_be_hex_hashes()
    {
        Assert.True(BlobIds.TryNormalizeHash(new string('A', 64), out var n));
        Assert.Equal(new string('a', 64), n);
        Assert.False(BlobIds.TryNormalizeHash("../" + new string('a', 61), out _));
        Assert.False(BlobIds.TryNormalizeUploadId(null, out _));
    }

    private sealed class Grant(Guid gc, Guid viewer, long through) : IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal v, CancellationToken ct) =>
            ValueTask.FromResult(gcId == gc && v.Id == viewer ? GcReadGrant.Through(through) : GcReadGrant.Denied);
    }

    private sealed class Links(BlobGcLink link) : IBlobGcLinks
    {
        public ValueTask<IReadOnlyList<BlobGcLink>> FindAsync(string hash, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<BlobGcLink>>([link]);
    }
}
