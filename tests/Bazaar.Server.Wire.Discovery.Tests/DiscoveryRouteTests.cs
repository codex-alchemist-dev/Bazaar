using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Wire.Abstractions;
using Bazaar.Wire.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Discovery.Tests;

public class DiscoveryRouteTests
{
    private const string _route = "/.well-known/bazaar/wire";
    private static readonly string[] _transports = ["ws", "quic"];
    private static readonly string[] _codecs = ["json", "cbor"];

    private static Task<DiscoveryHost> Start(Action<WireDiscoveryOptions>? configure = null, TimeProvider? time = null) =>
        DiscoveryHost.StartAsync(configure, s => s.AddSingleton<IWireDiscoveryContribution>(new FakeContribution(_transports, _codecs)), time);

    [Fact]
    public async Task Document_lists_transports_codecs_protocol_and_schemas()
    {
        await using var host = await Start();
        var source = new EmbeddedWireSchemaSource();

        var response = await host.Client.GetAsync(_route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;

        Assert.Equal(source.Info.Major, root.GetProperty("protocol").GetProperty("major").GetInt32());
        Assert.Equal(source.Info.Minor, root.GetProperty("protocol").GetProperty("minor").GetInt32());
        Assert.Equal(source.Info.Hash, root.GetProperty("schemaHash").GetString());
        Assert.Equal(_transports, root.GetProperty("transports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(_codecs, root.GetProperty("codecs").EnumerateArray().Select(e => e.GetString()));

        var schemas = root.GetProperty("schemas").EnumerateArray().ToArray();
        Assert.Equal(source.Names, schemas.Select(s => s.GetProperty("name").GetString()));
        var cddl = schemas.Single(s => s.GetProperty("name").GetString() == "wire.cddl");
        Assert.True(source.TryGet("wire.cddl", out var artifact));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(artifact.Content.Span)), cddl.GetProperty("sha256").GetString());
        Assert.Equal(artifact.Content.Length, cddl.GetProperty("size").GetInt32());
        Assert.Equal(_route + "/schema/wire.cddl", cddl.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Every_listed_url_serves_its_artifact_with_the_listed_hash()
    {
        // One request for the document and one per artifact. The operator default (300 a minute, doc 90 section 6) is below the
        // artifact count now, so this test sets a budget that covers one full mirror; the default itself is unchanged.
        var budget = new EmbeddedWireSchemaSource().Names.Count + 1;
        await using var host = await Start(o => o.PermitLimit = budget);
        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync(_route, TestContext.Current.CancellationToken));

        foreach (var schema in doc.RootElement.GetProperty("schemas").EnumerateArray())
        {
            var bytes = await host.Client.GetByteArrayAsync(schema.GetProperty("url").GetString(), TestContext.Current.CancellationToken);
            Assert.Equal(schema.GetProperty("sha256").GetString(), "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public async Task Schema_fetch_returns_bytes_content_type_and_nested_names()
    {
        await using var host = await Start();
        var source = new EmbeddedWireSchemaSource();

        var response = await host.Client.GetAsync(_route + "/schema/schema.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(source.TryGet("schema.json", out var expected));
        Assert.Equal(expected.Content.ToArray(), await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        var nested = source.Names.First(n => n.StartsWith("vectors/", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(_route + "/schema/" + nested, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/schema/nope.json")]
    [InlineData("/schema/")]
    [InlineData("/schema/../schema.json")]
    [InlineData("/schema/vectors")]
    [InlineData("/schema/WIRE.CDDL")]
    public async Task Unknown_schema_name_is_404(string suffix)
    {
        await using var host = await Start();

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(_route + suffix, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Document_has_etag_cache_headers_and_answers_304()
    {
        await using var host = await Start();

        var first = await host.Client.GetAsync(_route, TestContext.Current.CancellationToken);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag.IsWeak);
        Assert.Contains("max-age=300", first.Headers.CacheControl?.ToString());
        Assert.Contains("public", first.Headers.CacheControl?.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, _route);
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await host.Client.SendAsync(conditional, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(etag, second.Headers.ETag);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        using var stale = new HttpRequestMessage(HttpMethod.Get, _route);
        stale.Headers.TryAddWithoutValidation("If-None-Match", "\"other\", W/\"also-other\"");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(stale, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Schema_etag_derives_from_the_artifact_hash_and_answers_304()
    {
        await using var host = await Start();
        var source = new EmbeddedWireSchemaSource();
        Assert.True(source.TryGet("wire.cddl", out var artifact));

        var response = await host.Client.GetAsync(_route + "/schema/wire.cddl", TestContext.Current.CancellationToken);
        Assert.Equal("\"sha256:" + Convert.ToHexStringLower(SHA256.HashData(artifact.Content.Span)) + "\"", response.Headers.ETag?.ToString());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, _route + "/schema/wire.cddl");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.NotModified, (await host.Client.SendAsync(conditional, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Etag_changes_when_the_enabled_modules_change()
    {
        var contribution = new FakeContribution(_transports, _codecs);
        await using var host = await DiscoveryHost.StartAsync(services: s => s.AddSingleton<IWireDiscoveryContribution>(contribution));

        var before = (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).Headers.ETag;
        contribution.Transports = ["ws"];
        var afterResponse = await host.Client.GetAsync(_route, TestContext.Current.CancellationToken);

        Assert.NotEqual(before, afterResponse.Headers.ETag);
        using var doc = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["ws"], doc.RootElement.GetProperty("transports").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Disabled_answers_404_on_both_routes()
    {
        await using var host = await Start(o => o.Enabled = false);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(_route + "/schema/schema.json", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Disabled_is_read_on_every_request()
    {
        WireDiscoveryOptions? live = null;
        await using var host = await DiscoveryHost.StartAsync(services: s => s.PostConfigure<WireDiscoveryOptions>(o => live = o));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);

        // IOptionsMonitor caches the instance; changing it in place models a settings reload.
        Assert.NotNull(live);
        live.Enabled = false;
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after_then_recovers()
    {
        var time = new ManualTime();
        await using var host = await Start(o =>
        {
            o.PermitLimit = 3;
            o.Window = TimeSpan.FromSeconds(30);
        }, time);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);
        }

        var refused = await host.Client.GetAsync(_route + "/schema/schema.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(30, refused.Headers.RetryAfter?.Delta?.TotalSeconds);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_is_per_client_address()
    {
        await using var host = await Start(o => o.PermitLimit = 1);

        Assert.Equal(HttpStatusCode.OK, (await Get("198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get("198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("198.51.100.2")).StatusCode);

        Task<HttpResponseMessage> Get(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _route);
            request.Headers.Add("X-Test-Client", ip);
            return host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Tracked_client_cap_bounds_memory_by_sharing_one_bucket()
    {
        await using var host = await Start(o =>
        {
            o.PermitLimit = 1;
            o.MaxTrackedClients = 2;
        });

        Assert.Equal(HttpStatusCode.OK, (await Get("198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("198.51.100.2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("198.51.100.3")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get("198.51.100.4")).StatusCode);

        Task<HttpResponseMessage> Get(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _route);
            request.Headers.Add("X-Test-Client", ip);
            return host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Unknown_or_malformed_module_names_are_not_published()
    {
        var contribution = new FakeContribution(["ws", "http://internal.example:5000", "ws", "Quic"], ["json", "", "cbor"]);
        await using var host = await DiscoveryHost.StartAsync(services: s => s.AddSingleton<IWireDiscoveryContribution>(contribution));

        var text = await host.Client.GetStringAsync(_route, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(text);

        Assert.Equal(["ws"], doc.RootElement.GetProperty("transports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["json", "cbor"], doc.RootElement.GetProperty("codecs").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("internal", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_carry_no_server_or_host_details()
    {
        await using var host = await Start();

        var response = await host.Client.GetAsync(_route, TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Base_path_is_configurable()
    {
        await using var host = await Start(o => o.BasePath = "/status/wire");

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/status/wire", TestContext.Current.CancellationToken));
        Assert.StartsWith("/status/wire/schema/", doc.RootElement.GetProperty("schemas")[0].GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(_route, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Only_get_is_mapped()
    {
        await using var host = await Start();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.PostAsync(_route, null, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(0, 60, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, 60, 0, 0)]
    [InlineData(1, 60, 1, -1)]
    public void Invalid_options_are_refused(int permits, int windowSeconds, int maxClients, int maxAge)
    {
        var options = new WireDiscoveryOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromSeconds(windowSeconds),
            MaxTrackedClients = maxClients,
            CacheMaxAgeSeconds = maxAge,
        };

        Assert.False(options.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Invalid_options_fail_startup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => Start(o => o.PermitLimit = 0));
    }
}
