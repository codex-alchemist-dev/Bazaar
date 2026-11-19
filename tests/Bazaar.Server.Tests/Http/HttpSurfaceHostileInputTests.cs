using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Blobs;
using Bazaar.Server.Constellation;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Http;
using Bazaar.Server.Providers;
using Bazaar.Server.Tests.Wire.Harness;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Http;

/// <summary>
/// Hostile requests for the authenticated HTTP surface over raw sockets (chaos round 2): no or wrong credentials, wrong methods and
/// content types, truncated, huge, NUL-laden and non-UTF-8 bodies, and racing uploads. Every request ends in a designed status,
/// never a 500, and the server logs no unhandled exception.
/// </summary>
public sealed class HttpSurfaceHostileInputTests : IAsyncDisposable
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-00000000c001");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-http-hostile-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _errors = [];

    public ValueTask DisposeAsync()
    {
        // The invariant of the whole class: no unhandled exception, whatever the client sent.
        lock (_errors)
        {
            Assert.Empty(_errors);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FixedSearch : ISearchProvider
    {
        public string Kind => "fixed";

        public ProviderStatus Probe() => new(Kind, true, [], null);

        public Task IndexAsync(SearchDocument doc, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchHit>> QueryAsync(SearchQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<SearchHit>>([new("1", 0.9)]);
    }

    private sealed class Grants : IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) =>
            ValueTask.FromResult(gcId == _gc && viewer.Id == _alice ? GcReadGrant.Through(5) : GcReadGrant.Denied);
    }

    private async Task<(WebApplication App, int Port)> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _root, ["Bazaar:Http:MaxBlobBytes"] = "100000", ["Bazaar:Http:MaxChunkBytes"] = "4096" };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.AddProvider(new ErrorSink(_errors));
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuthenticator().Allow("alice", new PrincipalRef(_alice, "human")));
        builder.Services.AddSingleton<IGcReadAccess, Grants>();
        builder.Services.AddSingleton<ISearchProvider, FixedSearch>();
        builder.Services.AddBazaarBlobs(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        new ConstellationComponent().AddServices(builder.Services, builder.Configuration);
        var app = builder.Build();
        app.MapBazaarHttpSurface();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address).Port);
    }

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private sealed record Reply(int Status, string Head, string Body);

    /// <summary>Sends a whole request and reads the answer to the end of the connection (status 0: closed without an answer).</summary>
    private static async Task<Reply> RawAsync(int port, byte[] request, params byte[][] more)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        var stream = client.GetStream();
        try
        {
            await stream.WriteAsync(request, Ct);
            foreach (var part in more)
            {
                await stream.WriteAsync(part, Ct);
            }
        }
        catch (IOException)
        {
            // The server answered early and closed; read the answer below.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var received = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var got = await stream.ReadAsync(buffer, timeout.Token);
                if (got == 0)
                {
                    break;
                }

                received.Write(buffer, 0, got);
            }
        }
        catch (IOException)
        {
        }

        var text = Encoding.Latin1.GetString(received.ToArray());
        if (!text.StartsWith("HTTP/1.1 ", StringComparison.Ordinal))
        {
            return new Reply(0, string.Empty, string.Empty);
        }

        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? string.Empty : text[(split + 4)..];
        if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = Dechunk(body);
        }

        return new Reply(int.Parse(text.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture), head, body);
    }

    private static string Dechunk(string body)
    {
        var result = new StringBuilder();
        var at = 0;
        while (at < body.Length)
        {
            var eol = body.IndexOf("\r\n", at, StringComparison.Ordinal);
            if (eol < 0 || !int.TryParse(body.AsSpan(at, eol - at), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var size) || size == 0)
            {
                break;
            }

            result.Append(body, eol + 2, Math.Min(size, body.Length - eol - 2));
            at = eol + 2 + size + 2;
        }

        return result.ToString();
    }

    private static byte[] Request(string method, string path, string? token, string headers = "", byte[]? body = null)
    {
        var text = $"{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n"
            + (token is null ? string.Empty : "Authorization: Bearer " + token + "\r\n")
            + headers
            + (body is null ? string.Empty : "Content-Length: " + body.Length + "\r\n")
            + "\r\n";
        return body is null ? Bytes(text) : [.. Bytes(text), .. body];
    }

    private static void AssertDesigned(Reply reply, params int[] allowed) =>
        Assert.True(allowed.Contains(reply.Status), $"status {reply.Status} not in [{string.Join(",", allowed)}]: {reply.Body}");

    [Theory]
    [InlineData("POST", "/blobs")]
    [InlineData("PATCH", "/blobs/uploads/00000000000000000000000000000000")]
    [InlineData("HEAD", "/blobs/uploads/00000000000000000000000000000000")]
    [InlineData("GET", "/blobs/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("GET", "/search?gc=00000000-0000-7000-8000-00000000c001&q=x")]
    [InlineData("POST", "/constellation/tiles")]
    [InlineData("GET", "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=x")]
    [InlineData("GET", "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001&ordinal=0")]
    public async Task Hostile_credentials_are_a_401_and_never_reach_the_body(string method, string path)
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var big = new byte[200_000];
            foreach (var header in new[]
            {
                null,
                "Authorization: Bearer \r\n",
                "Authorization: Bearer wrong\r\n",
                "Authorization: Basic YWxpY2U6eA==\r\n",
                "Authorization: bearer\talice\r\n",
                "Authorization: Bearer alice, Bearer alice\r\n",
                "Authorization: Bearer aliÿce\r\n",
                "Authorization: Bearer " + new string('a', 20_000) + "\r\n",
            })
            {
                var head = $"{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n{header}Content-Length: {big.Length}\r\nUpload-Length: 5\r\nUpload-Offset: 0\r\n\r\n";
                var reply = await RawAsync(port, [.. Bytes(head), .. big]);

                AssertDesigned(reply, 0, 400, 401, 431);
            }

            // Two Authorization headers: the first is wrong, the second right. It must not authenticate.
            var doubled = await RawAsync(port, Bytes($"{method} {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nAuthorization: Bearer nobody\r\nAuthorization: Bearer alice\r\n\r\n"));
            AssertDesigned(doubled, 401);
        }
    }

    [Theory]
    [InlineData("GET", "/blobs")]
    [InlineData("DELETE", "/blobs/uploads/00000000000000000000000000000000")]
    [InlineData("PUT", "/blobs")]
    [InlineData("POST", "/blobs/0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("POST", "/search")]
    [InlineData("PUT", "/constellation/tiles")]
    [InlineData("GET", "/constellation/tiles")]
    [InlineData("GET", "/auth/refresh")]
    [InlineData("DELETE", "/vault/doc")]
    [InlineData("PATCH", "/vault/search")]
    [InlineData("TRACE", "/blobs")]
    [InlineData("BREW", "/blobs")]
    public async Task Wrong_methods_are_a_405_or_404(string method, string path)
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var reply = await RawAsync(port, Request(method, path, "alice", body: method is "GET" or "TRACE" ? null : Bytes("{}")));

            AssertDesigned(reply, 404, 405);
        }
    }

    [Theory]
    [InlineData("{\"view_id\":\"v1\",\"graph_ver")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"view_id\":\"v\u0000x\",\"tier\":\"Far\"}")]
    [InlineData("{\"view_id\":\"v1\",\"graph_version\":1e999,\"tier\":\"Far\"}")]
    [InlineData("{\"view_id\":\"v1\",\"graph_version\":-1,\"tier\":\"Far\",\"min_x\":1e308,\"max_x\":-1e308}")]
    [InlineData("{\"view_id\":\"v1\",\"tier\":\"Far\",\"max_nodes\":99999999999999999999}")]
    [InlineData("{\"view_id\":\"v1\",\"tier\":\"Far\",\"view_id\":\"v2\"}")]
    public async Task Tile_bodies_that_are_not_a_viewport_are_a_400(string json)
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            foreach (var type in new[] { "application/json", "text/plain", "application/octet-stream" })
            {
                var reply = await RawAsync(port, Request("POST", "/constellation/tiles", "alice", $"Content-Type: {type}\r\n", Encoding.UTF8.GetBytes(json)));

                AssertDesigned(reply, 400, 200);
            }
        }
    }

    [Fact]
    public async Task Tile_bodies_in_hostile_encodings_deep_nesting_and_over_the_cap_are_refused()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            // Invalid UTF-8 inside a string, a UTF-16 byte order mark and NUL bytes.
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", [.. Bytes("{\"view_id\":\""), 0xC3, 0x28, 0xFF, 0xFE, .. Bytes("\",\"tier\":\"Far\"}")])), 400, 200);
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("{}")])), 400);
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", new byte[2000])), 400);

            // Nesting far past the reader's depth limit (3000 bytes, under the body cap).
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", Bytes(new string('[', 2000)))), 400);
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", Bytes("{\"a\":" + new string('[', 1500)))), 400);

            // 10 MB: refused from the declared length, before the body is read.
            AssertDesigned(await RawAsync(port, Request("POST", "/constellation/tiles", "alice", "Content-Type: application/json\r\n", new byte[10 * 1024 * 1024])), 0, 413);

            // No declared length at all (chunked), and a lying second Content-Length.
            AssertDesigned(await RawAsync(port, Bytes("POST /constellation/tiles HTTP/1.1\r\nHost: x\r\nConnection: close\r\nAuthorization: Bearer alice\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n")), 400);
            AssertDesigned(await RawAsync(port, Bytes("POST /constellation/tiles HTTP/1.1\r\nHost: x\r\nConnection: close\r\nAuthorization: Bearer alice\r\nContent-Length: 2\r\nContent-Length: 99999999\r\n\r\n{}")), 0, 400);
        }
    }

    [Fact]
    public async Task Auth_refresh_survives_truncated_huge_nul_and_non_utf8_bodies_without_credentials()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var cases = new List<byte[]>
            {
                Bytes("{\"refresh_token\":\"abc"),
                Bytes("{\"refresh_token\":\"a\\u0000b\",\"device_class\":\"desktop\"}"),
                Join(Bytes("{\"refresh_token\":\""), [0xC3, 0x28, 0xFF], Bytes("\",\"device_class\":\"d\"}")),
                Join(Bytes("{\"refresh_token\":\"x\",\"device_class\":\"d\"}"), [0, 0, 0]),
                Bytes(new string('[', 5000)),
                Bytes("{\"refresh_token\":1,\"device_class\":[]}"),
                Bytes("{\"refresh_token\":null,\"device_class\":null}"),
            };
            foreach (var body in cases)
            {
                var reply = await RawAsync(port, Request("POST", "/auth/refresh", null, "Content-Type: application/json\r\n", body));

                // 503 is the designed answer on a host with no token service registered.
                AssertDesigned(reply, 400, 401, 503);
            }
        }
    }

    [Fact]
    public async Task Auth_refresh_refuses_a_body_far_bigger_than_any_refresh_request_before_buffering_it()
    {
        // The route takes no credential, so anyone can open it; each connection used to buffer up to the server-wide 30 MB cap.
        var (app, port) = await StartAsync();
        await using (app)
        {
            var huge = Bytes("{\"refresh_token\":\"" + new string('x', 10 * 1024 * 1024) + "\",\"device_class\":\"d\"}");
            AssertDesigned(await RawAsync(port, Request("POST", "/auth/refresh", null, "Content-Type: application/json\r\n", huge)), 0, 413);

            // No declared length: the cap is enforced while the body is read.
            var chunk = new string('x', 1024 * 1024);
            var chunked = "POST /auth/refresh HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"
                + string.Concat(Enumerable.Repeat("100000\r\n" + chunk + "\r\n", 3)) + "0\r\n\r\n";
            AssertDesigned(await RawAsync(port, Bytes(chunked)), 0, 413);
        }
    }

    [Fact]
    public async Task Upload_headers_that_lie_are_refused()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            foreach (var length in new[] { "0", "-1", "+5", "5.5", "0x10", "99999999999999999999", "100001", "1,2", " ", "ÿ" })
            {
                var reply = await RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: " + length + "\r\n"));

                AssertDesigned(reply, 400, 413);
            }

            AssertDesigned(await RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: 5\r\nUpload-Length: 6\r\n")), 400);
            AssertDesigned(await RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: 5\r\nUpload-Sha256: " + new string('g', 64) + "\r\n")), 400);
            AssertDesigned(await RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: 5\r\nUpload-Sha256: " + new string('a', 63) + "\r\n")), 400);
            AssertDesigned(await RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: 5\r\nUpload-Sha256: " + new string('a', 64) + new string('b', 5000) + "\r\n")), 400);
            Assert.False(Directory.Exists(Path.Combine(_root, "uploads")) && Directory.EnumerateFiles(Path.Combine(_root, "uploads")).Any());
        }
    }

    [Fact]
    public async Task Chunks_with_lying_offsets_and_lengths_never_corrupt_an_upload()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var data = new byte[3000];
            Random.Shared.NextBytes(data);
            var created = await RawAsync(port, Request("POST", "/blobs", "alice", $"Upload-Length: {data.Length}\r\nUpload-Sha256: {Sha(data)}\r\n"));
            Assert.Equal(201, created.Status);
            var id = System.Text.Json.JsonElement.Parse(created.Body).GetProperty("upload_id").GetString()!;
            var path = "/blobs/uploads/" + id;

            foreach (var offset in new[] { "-1", "99999999999999999999", "9223372036854775807", "1", "abc", "", "0,0" })
            {
                AssertDesigned(await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: " + offset + "\r\n", data[..100])), 400, 409);
            }

            // Zero-length, over-long and chunked bodies.
            AssertDesigned(await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: 0\r\n", [])), 400, 411);
            AssertDesigned(await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: 0\r\n", new byte[4097])), 413);
            AssertDesigned(await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: 0\r\n", new byte[data.Length + 1])), 400, 413);
            AssertDesigned(await RawAsync(port, Bytes($"PATCH {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nAuthorization: Bearer alice\r\nUpload-Offset: 0\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n")), 411);

            // A body that stops short of its Content-Length: the half chunk is dropped and the offset stays put.
            var short1 = await RawAsync(port, Bytes($"PATCH {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\nAuthorization: Bearer alice\r\nUpload-Offset: 0\r\nContent-Length: 1000\r\n\r\n"), data[..300]);
            AssertDesigned(short1, 0, 400);
            await Task.Delay(200, Ct);
            var head = await RawAsync(port, Request("HEAD", path, "alice"));
            Assert.Contains("Upload-Offset: 0", head.Head, StringComparison.OrdinalIgnoreCase);

            // The intact upload still completes, byte for byte.
            Assert.Equal(204, (await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: 0\r\n", data[..1500]))).Status);
            Assert.Equal(200, (await RawAsync(port, Request("PATCH", path, "alice", "Upload-Offset: 1500\r\n", data[1500..]))).Status);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var get = new HttpRequestMessage(HttpMethod.Get, "/blobs/" + Sha(data));
            get.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            Assert.Equal(data, await (await client.SendAsync(get, Ct)).Content.ReadAsByteArrayAsync(Ct));
        }
    }

    [Theory]
    [InlineData("bytes=")]
    [InlineData("bytes=-")]
    [InlineData("bytes=9-1")]
    [InlineData("bytes=99999999999999999999-")]
    [InlineData("bytes=0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0,0-0")]
    [InlineData("bytes=-99999999999999999999")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1\u0000")]
    public async Task Hostile_range_headers_are_answered_with_a_designed_status(string range)
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            var created = await RawAsync(port, Request("POST", "/blobs", "alice", $"Upload-Length: {data.Length}\r\n"));
            var id = System.Text.Json.JsonElement.Parse(created.Body).GetProperty("upload_id").GetString()!;
            Assert.Equal(200, (await RawAsync(port, Request("PATCH", "/blobs/uploads/" + id, "alice", "Upload-Offset: 0\r\n", data))).Status);

            var reply = await RawAsync(port, Request("GET", "/blobs/" + Sha(data), "alice", "Range: " + range + "\r\n"));

            AssertDesigned(reply, 0, 200, 206, 400, 416);
        }
    }

    [Fact]
    public async Task Hostile_query_strings_on_search_and_vault_routes_are_a_400_or_404()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            foreach (var path in new[]
            {
                "/search?gc=%FF%FE&q=x",
                "/search?gc=00000000-0000-7000-8000-00000000c001&q=%00",
                "/search?gc=00000000-0000-7000-8000-00000000c001&q=%FF%C3%28",
                "/search?gc=00000000-0000-7000-8000-00000000c001&q=" + new string('a', 201),
                "/search?gc=00000000-0000-7000-8000-00000000c001&q=x&limit=99999999999999999999",
                "/search?gc=00000000-0000-7000-8000-00000000c001&q=x&limit=-1",
                "/search?gc=00000000-0000-7000-8000-00000000c001&gc=00000000-0000-7000-8000-00000000c002&q=x",
                "/search?q=x&q=y&gc=zzz",
                "/vault/search?vault=%00&q=x",
                "/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=%FF",
                "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001&ordinal=99999999999999999999",
                "/vault/doc?vault=00000000-0000-7000-8000-00000000d001&doc=00000000-0000-7000-8000-00000000e001&ordinal=-1",
                "/blobs/%00",
                "/blobs/" + new string('a', 5000),
                "/blobs/uploads/%FF%FE",
            })
            {
                AssertDesigned(await RawAsync(port, Request("GET", path, "alice")), 200, 400, 404, 405, 414, 503);
            }
        }
    }

    // Chaos round 3: NUL is not valid in Postgres text, so a query or filter holding one would reach the database and fail as a 500.
    [Theory]
    [InlineData("/search?gc=00000000-0000-7000-8000-00000000c001&q=a%00b")]
    [InlineData("/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=a%00b")]
    [InlineData("/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=x&project=a%00b")]
    [InlineData("/vault/search?vault=00000000-0000-7000-8000-00000000d001&q=x&type=a%00b")]
    public async Task A_nul_in_a_search_query_or_filter_is_a_400(string path)
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            Assert.Equal(400, (await RawAsync(port, Request("GET", path, "alice"))).Status);
        }
    }

    [Fact]
    public async Task Racing_uploads_of_the_same_bytes_all_complete_and_the_blob_has_one_owner_line_each()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            var data = new byte[3000];
            Random.Shared.NextBytes(data);
            var sha = Sha(data);
            const int Racers = 6;
            var ids = new List<string>();
            for (var i = 0; i < Racers; i++)
            {
                var created = await RawAsync(port, Request("POST", "/blobs", "alice", $"Upload-Length: {data.Length}\r\nUpload-Sha256: {sha}\r\n"));
                Assert.Equal(201, created.Status);
                ids.Add(System.Text.Json.JsonElement.Parse(created.Body).GetProperty("upload_id").GetString()!);
            }

            var first = ids.Select(id => RawAsync(port, Request("PATCH", "/blobs/uploads/" + id, "alice", "Upload-Offset: 0\r\n", data[..2000]))).ToArray();
            Assert.All(await Task.WhenAll(first), r => Assert.Equal(204, r.Status));

            // The last chunk of every upload lands at the same moment: each commits the same content.
            var gate = new TaskCompletionSource();
            var last = ids.Select(async id =>
            {
                await gate.Task;
                return await RawAsync(port, Request("PATCH", "/blobs/uploads/" + id, "alice", "Upload-Offset: 2000\r\n", data[2000..]));
            }).ToArray();
            gate.SetResult();
            var replies = await Task.WhenAll(last);

            Assert.All(replies, r => Assert.Equal(200, r.Status));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var get = new HttpRequestMessage(HttpMethod.Get, "/blobs/" + sha);
            get.Headers.TryAddWithoutValidation("Authorization", "Bearer alice");
            Assert.Equal(data, await (await client.SendAsync(get, Ct)).Content.ReadAsByteArrayAsync(Ct));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "uploads")));
        }
    }

    [Fact]
    public async Task A_burst_of_upload_creations_cannot_exceed_the_per_principal_quota()
    {
        var (app, port) = await StartAsync(new Dictionary<string, string?> { ["Bazaar:Http:MaxActiveUploadsPerPrincipal"] = "3", ["Bazaar:Http:RequestsPerSecond"] = "1000", ["Bazaar:Http:RequestBurst"] = "1000" });
        await using (app)
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => RawAsync(port, Request("POST", "/blobs", "alice", "Upload-Length: 10\r\n"))));

            Assert.Equal(3, replies.Count(r => r.Status == 201));
            Assert.All(replies.Where(r => r.Status != 201), r => Assert.Equal(429, r.Status));
            Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(_root, "uploads"), "*.meta").Count());
        }
    }

    [Fact]
    public async Task A_client_that_aborts_the_last_chunk_cannot_strand_a_complete_upload()
    {
        var (app, port) = await StartAsync();
        await using (app)
        {
            // Everything is on disk but the commit never ran (the request was aborted between the last write and the rename).
            var data = new byte[500];
            Random.Shared.NextBytes(data);
            var created = await RawAsync(port, Request("POST", "/blobs", "alice", $"Upload-Length: {data.Length}\r\nUpload-Sha256: {Sha(data)}\r\n"));
            var id = System.Text.Json.JsonElement.Parse(created.Body).GetProperty("upload_id").GetString()!;
            await File.WriteAllBytesAsync(Path.Combine(_root, "uploads", id + ".part"), data, Ct);

            var head = await RawAsync(port, Request("HEAD", "/blobs/uploads/" + id, "alice"));
            Assert.Contains("Upload-Offset: 500", head.Head, StringComparison.OrdinalIgnoreCase);

            // The client retries its last chunk. The server finishes the commit instead of answering a conflict forever.
            var retry = await RawAsync(port, Request("PATCH", "/blobs/uploads/" + id, "alice", "Upload-Offset: 300\r\n", data[300..]));

            Assert.Equal(200, retry.Status);
            Assert.Contains(Sha(data), retry.Body, StringComparison.Ordinal);
        }
    }

    private sealed class ErrorSink(List<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(errors, categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(List<string> errors, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    lock (errors)
                    {
                        errors.Add(category + ": " + exception?.GetType().Name + " " + formatter(state, exception));
                    }
                }
            }
        }
    }
}
