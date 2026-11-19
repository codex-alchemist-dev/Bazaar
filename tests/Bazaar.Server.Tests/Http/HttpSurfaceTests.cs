using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Constellation;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Http;
using Bazaar.Server.Http.Blobs;
using Bazaar.Server.Providers;
using Bazaar.Server.Tests.Wire.Harness;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Http;

public sealed class HttpSurfaceTests : IAsyncLifetime
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly Guid _readableGc = Guid.Parse("00000000-0000-7000-8000-00000000c001");
    private static readonly Guid _secretGc = Guid.Parse("00000000-0000-7000-8000-00000000c002");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-http-" + Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return ValueTask.CompletedTask;
    }

    private sealed class FixedSearch : ISearchProvider
    {
        public string Kind => "fixed";

        public ProviderStatus Probe() => new(Kind, true, [], null);

        public Task IndexAsync(SearchDocument doc, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([new("1", 0.9), new("5", 0.5), new("9", 0.1)]);
    }

    private sealed class GrantTable : IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) => ValueTask.FromResult(
            gcId == _readableGc && viewer.Id == _alice ? GcReadGrant.Through(5) : GcReadGrant.Denied);
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<Dictionary<string, string?>>? settings = null, Action<WireOptions>? wire = null)
    {
        var values = new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _root, ["Bazaar:Http:MaxBlobBytes"] = "1000", ["Bazaar:Http:MaxChunkBytes"] = "100" };
        settings?.Invoke(values);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddBazaarWire(builder.Configuration);
        if (wire is not null) builder.Services.Configure(wire);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuthenticator()
            .Allow("alice", new PrincipalRef(_alice, "human")).Allow("bob", new PrincipalRef(_bob, "human")));
        builder.Services.AddSingleton<IGcReadAccess, GrantTable>();
        builder.Services.AddSingleton<ISearchProvider, FixedSearch>();
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        new ConstellationComponent().AddServices(builder.Services, builder.Configuration);
        var app = builder.Build();
        app.MapBazaarHttpSurface();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private const string _tileBody = """{"view_id":"v1","graph_version":0,"tier":"Far","min_x":-1,"min_y":-1,"min_z":-1,"max_x":1,"max_y":1,"max_z":1,"max_nodes":10}""";

    [Fact]
    public async Task Tile_endpoint_requires_a_token()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Req(HttpMethod.Post, "/constellation/tiles", null, Encoding.UTF8.GetBytes(_tileBody)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Tile_endpoint_returns_a_binary_frame_for_an_empty_graph()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Req(HttpMethod.Post, "/constellation/tiles", "alice", Encoding.UTF8.GetBytes(_tileBody)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-bazaar-tile", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Tile_endpoint_rejects_a_bad_body_and_an_inverted_box()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var bad = await client.SendAsync(Req(HttpMethod.Post, "/constellation/tiles", "alice", Encoding.UTF8.GetBytes("{not json")), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            var inverted = _tileBody.Replace("\"min_x\":-1", "\"min_x\":5", StringComparison.Ordinal);
            var response = await client.SendAsync(Req(HttpMethod.Post, "/constellation/tiles", "alice", Encoding.UTF8.GetBytes(inverted)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string? token = "alice", byte[]? body = null, params (string, string)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        if (body is not null) request.Content = new ByteArrayContent(body);
        return request;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task<string> OpenAsync(HttpClient client, long length, string? sha = null, string token = "alice")
    {
        var headers = sha is null ? new[] { ("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture)) } : [("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Upload-Sha256", sha)];
        var response = await client.SendAsync(Req(HttpMethod.Post, "/blobs", token, null, headers), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("upload_id").GetString()!;
    }

    private static Task<HttpResponseMessage> ChunkAsync(HttpClient client, string id, long offset, byte[] data, string token = "alice") =>
        client.SendAsync(Req(HttpMethod.Patch, $"/blobs/uploads/{id}", token, data, ("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture))), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("POST", "/blobs")]
    [InlineData("PATCH", "/blobs/uploads/00000000000000000000000000000000")]
    [InlineData("HEAD", "/blobs/uploads/00000000000000000000000000000000")]
    [InlineData("GET", "/blobs/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("GET", "/search?gc=00000000-0000-7000-8000-00000000c001&q=x")]
    [InlineData("GET", "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=x")]
    [InlineData("GET", "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001&ordinal=0")]
    public async Task Unauthenticated_requests_are_rejected(string method, string url)
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var none = await client.SendAsync(Req(new HttpMethod(method), url, null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
            var bad = await client.SendAsync(Req(new HttpMethod(method), url, "wrong"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
    }

    [Fact]
    public async Task Vault_search_rejects_bad_parameters_before_searching()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var noVault = await client.SendAsync(Req(HttpMethod.Get, "/vault/search?q=auth"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, noVault.StatusCode);
            var emptyQuery = await client.SendAsync(Req(HttpMethod.Get, "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q="), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, emptyQuery.StatusCode);
            var longQuery = await client.SendAsync(Req(HttpMethod.Get, "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=" + new string('x', 201)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, longQuery.StatusCode);
        }
    }

    [Fact]
    public async Task Vault_doc_rejects_missing_or_negative_parameters()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var missing = await client.SendAsync(Req(HttpMethod.Get, "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var notAGuid = await client.SendAsync(Req(HttpMethod.Get, "/vault/doc?vault=nope&doc=00000000-0000-7000-8000-00000000e001&ordinal=0"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, notAGuid.StatusCode);
        }
    }

    [Fact]
    public async Task Vault_doc_is_unavailable_when_the_search_component_is_not_registered()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Req(HttpMethod.Get, "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001&ordinal=0"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task Vault_search_is_unavailable_when_the_search_component_is_not_registered()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Req(HttpMethod.Get, "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=auth"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task Oversized_upload_is_rejected_before_any_body()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var tooBig = await client.SendAsync(Req(HttpMethod.Post, "/blobs", "alice", null, ("Upload-Length", "1001")), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

            var id = await OpenAsync(client, 500);
            var chunk = await ChunkAsync(client, id, 0, new byte[101]);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunk.StatusCode);
        }
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_nothing_is_committed()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var data = "hello blob"u8.ToArray();
            var id = await OpenAsync(client, data.Length, Sha("other"u8.ToArray()));
            var response = await ChunkAsync(client, id, 0, data);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var download = await client.SendAsync(Req(HttpMethod.Get, "/blobs/" + Sha(data)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        }
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..%5C..%5Cwindows")]
    [InlineData("%2e%2e")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("abc")]
    public async Task Path_traversal_looking_ids_are_rejected(string id)
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var blob = await client.SendAsync(Req(HttpMethod.Get, "/blobs/" + id), TestContext.Current.CancellationToken);
            Assert.True(blob.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
            var upload = await client.SendAsync(Req(HttpMethod.Head, "/blobs/uploads/" + id), TestContext.Current.CancellationToken);
            Assert.True(upload.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
            var body = await blob.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(_root, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Upload_resumes_from_the_reported_offset_and_download_supports_ranges()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var data = Enumerable.Range(0, 250).Select(i => (byte)i).ToArray();
            var id = await OpenAsync(client, data.Length, Sha(data));
            Assert.Equal(HttpStatusCode.NoContent, (await ChunkAsync(client, id, 0, data[..100])).StatusCode);

            // A wrong offset is refused and reports where to resume.
            var wrong = await ChunkAsync(client, id, 0, data[..100]);
            Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

            var head = await client.SendAsync(Req(HttpMethod.Head, $"/blobs/uploads/{id}"), TestContext.Current.CancellationToken);
            Assert.Equal("100", head.Headers.GetValues("Upload-Offset").Single());

            Assert.Equal(HttpStatusCode.NoContent, (await ChunkAsync(client, id, 100, data[100..200])).StatusCode);
            var done = await ChunkAsync(client, id, 200, data[200..]);
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            using var doc = JsonDocument.Parse(await done.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(Sha(data), doc.RootElement.GetProperty("hash").GetString());

            var full = await client.SendAsync(Req(HttpMethod.Get, "/blobs/" + Sha(data)), TestContext.Current.CancellationToken);
            Assert.Equal(data, await full.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var range = Req(HttpMethod.Get, "/blobs/" + Sha(data));
            range.Headers.Range = new RangeHeaderValue(10, 19);
            var partial = await client.SendAsync(range, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(data[10..20], await partial.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_non_viewer_gets_not_found_and_cannot_continue_another_principals_upload()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var data = "private bytes"u8.ToArray();
            var id = await OpenAsync(client, data.Length);
            Assert.Equal(HttpStatusCode.NotFound, (await ChunkAsync(client, id, 0, data, "bob")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(client, id, 0, data)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, "/blobs/" + Sha(data), "bob"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/blobs/" + Sha(data)), TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task Search_returns_only_readable_gcs_and_seqs()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var ok = await client.SendAsync(Req(HttpMethod.Get, $"/search?gc={_readableGc}&q=hello"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            // Seq 9 is past the viewer's left_seq (5).
            Assert.Equal([1L, 5L], doc.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("seq").GetInt64()).ToArray());

            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, $"/search?gc={_secretGc}&q=hello"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, $"/search?gc={_readableGc}&q=hello", "bob"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Get, $"/search?gc={_readableGc}"), TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task The_http_bucket_is_separate_from_the_wire_bucket()
    {
        // The wire allows a burst of 1; the HTTP surface a burst of 3. Three HTTP requests pass, the fourth is limited.
        var (app, client) = await StartAsync(s => { s["Bazaar:Http:RequestBurst"] = "3"; s["Bazaar:Http:RequestsPerSecond"] = "1"; }, w => { w.RequestBurst = 1; w.RequestsPerSecond = 1; });
        await using (app)
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Head, "/blobs/uploads/" + new string('a', 32)), TestContext.Current.CancellationToken)).StatusCode);
            }

            var limited = await client.SendAsync(Req(HttpMethod.Head, "/blobs/uploads/" + new string('a', 32)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.Contains("Retry-After"));

            // Another principal has its own tokens, and the limiter is not the wire's.
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Head, "/blobs/uploads/" + new string('a', 32), "bob"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.NotNull(app.Services.GetRequiredService<HttpRateLimiter>());
            Assert.Equal(1, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WireOptions>>().Value.RequestBurst);
        }
    }

    [Fact]
    public void Limiter_refills_with_time_and_does_not_share_state_with_other_instances()
    {
        var clock = new Bazaar.Testing.FakeClock();
        var options = new HttpSurfaceOptions { RequestBurst = 2, RequestsPerSecond = 1 };
        var a = new HttpRateLimiter(options, clock);
        var b = new HttpRateLimiter(options, clock);
        Assert.True(a.TryAcquire(_alice, out _));
        Assert.True(a.TryAcquire(_alice, out _));
        Assert.False(a.TryAcquire(_alice, out var wait));
        Assert.True(wait >= 1);
        Assert.True(b.TryAcquire(_alice, out _));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(a.TryAcquire(_alice, out _));
    }

    [Fact]
    public void Blob_ids_must_be_hex_hashes()
    {
        Assert.True(BlobIds.TryNormalizeHash(new string('A', 64), out var n));
        Assert.Equal(new string('a', 64), n);
        Assert.False(BlobIds.TryNormalizeHash(new string('a', 63), out _));
        Assert.False(BlobIds.TryNormalizeHash("../" + new string('a', 61), out _));
        Assert.False(BlobIds.TryNormalizeUploadId(null, out _));
    }
}
