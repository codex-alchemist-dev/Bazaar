using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http;
using Bazaar.Server.Tests.Components.PrivacyAudit;
using Bazaar.Server.Tests.Components.Remote;
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

/// <summary>The <c>/pair/*</c> routes (doc 32 section 3) over a real listener, with in-memory stores.</summary>
public sealed class PairingRoutesTests : IAsyncLifetime
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-0000000000b2");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var root = Path.Combine(Path.GetTempPath(), "bazaar-pairing-" + Guid.NewGuid().ToString("N"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Http:BlobRoot"] = root,
            ["Bazaar:Http:MaxBlobBytes"] = "1000",
            ["Bazaar:Http:MaxChunkBytes"] = "100",
        });
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuthenticator()
            .Allow("alice", new PrincipalRef(_alice, "human")).Allow("bob", new PrincipalRef(_bob, "human")));
        builder.Services.AddOptions<RemoteTokenOptions>();
        builder.Services.AddOptions<PairingOptions>();
        builder.Services.AddSingleton<IRefreshCredentialStore, InMemoryRefreshCredentialStore>();
        builder.Services.AddSingleton<IPairingOfferStore, InMemoryPairingOfferStore>();
        builder.Services.AddSingleton<IAccessTokenIssuer, FakeAccessTokenIssuer>();
        builder.Services.AddSingleton(new PrivacyAuditRecorder(new FakeAuditLog(), TimeProvider.System));
        builder.Services.AddSingleton<IPairingService, PairingService>();
        var app = builder.Build();
        app.MapBazaarPairingRoutes();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string? token, string body)
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text + "");
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, $"{(int)response.StatusCode}: {text}");
        Assert.True(response.IsSuccessStatusCode || doc.RootElement.TryGetProperty("error", out _), $"{(int)response.StatusCode}: {text}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Creating_an_offer_needs_a_signed_in_human()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Json(HttpMethod.Post, "/pair/offers", null, """{"role":"owner"}"""), Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_full_pairing_over_the_routes_ends_with_tokens_and_the_code_is_then_spent()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var created = await client.SendAsync(Json(HttpMethod.Post, "/pair/offers", "alice", """{"role":"owner"}"""), Ct);
            var offer = await ReadAsync(created);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var userCode = offer.GetProperty("user_code").GetString()!;
            var offerId = offer.GetProperty("offer_id").GetGuid();

            var start = await client.SendAsync(Json(HttpMethod.Post, "/pair/device-authorization", null,
                JsonSerializer.Serialize(new { user_code = userCode, label = "Dom laptop", device_class = "windows" })), Ct);
            var device = await ReadAsync(start);
            var deviceCode = device.GetProperty("device_code").GetString()!;

            var pending = await client.SendAsync(Json(HttpMethod.Post, "/pair/token", null, TokenBody(deviceCode)), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
            Assert.Equal("authorization_pending", (await ReadAsync(pending)).GetProperty("error").GetString());

            var approve = await client.SendAsync(Json(HttpMethod.Post, $"/pair/offers/{offerId:D}/approve", "alice", "{}"), Ct);
            Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

            var granted = await client.SendAsync(Json(HttpMethod.Post, "/pair/token", null, TokenBody(deviceCode)), Ct);
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
            var tokens = await ReadAsync(granted);
            Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
            Assert.StartsWith("access-", tokens.GetProperty("access_token").GetString(), StringComparison.Ordinal);
            Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);
            Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refresh_token").GetString()));

            var spent = await client.SendAsync(Json(HttpMethod.Post, "/pair/token", null, TokenBody(deviceCode)), Ct);
            Assert.Equal("invalid_grant", (await ReadAsync(spent)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Another_human_cannot_approve_the_offer()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var offer = await ReadAsync(await client.SendAsync(Json(HttpMethod.Post, "/pair/offers", "alice", """{"role":"coworker"}"""), Ct));
            var offerId = offer.GetProperty("offer_id").GetGuid();

            var response = await client.SendAsync(Json(HttpMethod.Post, $"/pair/offers/{offerId:D}/approve", "bob", "{}"), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("not_creator", (await ReadAsync(response)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_token_poll_with_the_wrong_grant_type_is_refused_before_the_service_is_asked()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var body = JsonSerializer.Serialize(new { device_code = "x", grant_type = "password" });

            var response = await client.SendAsync(Json(HttpMethod.Post, "/pair/token", null, body), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unsupported_grant_type", (await ReadAsync(response)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_malformed_body_is_a_bad_request()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Json(HttpMethod.Post, "/pair/device-authorization", null, "{not json"), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("malformed_request", (await ReadAsync(response)).GetProperty("error").GetString());
        }
    }

    private static string TokenBody(string deviceCode) =>
        JsonSerializer.Serialize(new { device_code = deviceCode, grant_type = PairingEndpoints.DeviceCodeGrantType });
}
