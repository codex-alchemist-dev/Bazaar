using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Http;

/// <summary>The <c>POST /auth/refresh</c> route (doc 32 section 5) over a real listener, with a fake token service.</summary>
public sealed class AuthRefreshEndpointsTests : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(ITokenService? tokens)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        if (tokens is not null) builder.Services.AddSingleton(tokens);
        var app = builder.Build();
        app.MapBazaarAuthRefreshRoutes();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static HttpRequestMessage Refresh(string body)
    {
        return new HttpRequestMessage(HttpMethod.Post, "/auth/refresh") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_live_refresh_secret_returns_the_new_pair_in_the_pairing_shape()
    {
        var fake = new FakeTokenService { Next = new TokenPair("access-test", DateTimeOffset.UtcNow.AddMinutes(15), "refresh-test-next", DateTimeOffset.UtcNow.AddDays(30)) };
        var (app, client) = await StartAsync(fake);
        await using (app)
        {
            var response = await client.SendAsync(Refresh("""{"refresh_token":"refresh-test","device_class":"windows","client_build":"0.1.0"}"""), Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.Equal("access-test", body.GetProperty("access_token").GetString());
            Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
            Assert.Equal("refresh-test-next", body.GetProperty("refresh_token").GetString());
            var expiresIn = body.GetProperty("expires_in").GetInt32();
            Assert.InRange(expiresIn, 800, 900);
            Assert.Equal("refresh-test", fake.LastRefresh);
            Assert.Equal("windows", fake.LastDevice!.DeviceClass);
            Assert.Equal("0.1.0", fake.LastDevice.ClientBuild);
        }
    }

    [Fact]
    public async Task A_refused_secret_answers_one_generic_invalid_grant_with_no_reason()
    {
        var fake = new FakeTokenService { Refuse = TokenRefreshFailure.ReuseDetected };
        var (app, client) = await StartAsync(fake);
        await using (app)
        {
            var response = await client.SendAsync(Refresh("""{"refresh_token":"refresh-test","device_class":"windows","client_build":"0.1.0"}"""), Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal("invalid_grant", (await ReadAsync(response)).GetProperty("error").GetString());
            Assert.DoesNotContain("reuse", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refresh-test", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task With_no_access_token_issuer_the_route_answers_503_unavailable_with_no_reason()
    {
        var fake = new FakeTokenService { Unavailable = true };
        var (app, client) = await StartAsync(fake);
        await using (app)
        {
            var response = await client.SendAsync(Refresh("""{"refresh_token":"refresh-test","device_class":"windows","client_build":"0.1.0"}"""), Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal("unavailable", (await ReadAsync(response)).GetProperty("error").GetString());
            Assert.DoesNotContain("refresh-test", text, StringComparison.Ordinal);
            Assert.DoesNotContain("issuer", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("""{"device_class":"windows","client_build":"0.1.0"}""")]
    [InlineData("""{"refresh_token":"   ","device_class":"windows","client_build":"0.1.0"}""")]
    [InlineData("""{"refresh_token":"refresh-test","client_build":"0.1.0"}""")]
    [InlineData("""{"refresh_token":"refresh-test","device_class":"","client_build":"0.1.0"}""")]
    public async Task A_missing_or_blank_refresh_token_or_device_class_is_a_bad_request(string body)
    {
        var fake = new FakeTokenService();
        var (app, client) = await StartAsync(fake);
        await using (app)
        {
            var response = await client.SendAsync(Refresh(body), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("malformed_request", (await ReadAsync(response)).GetProperty("error").GetString());
            Assert.Null(fake.LastRefresh);
        }
    }

    [Fact]
    public async Task Without_a_token_service_the_route_answers_unavailable_and_does_not_throw()
    {
        var (app, client) = await StartAsync(tokens: null);
        await using (app)
        {
            var response = await client.SendAsync(Refresh("""{"refresh_token":"refresh-test","device_class":"windows","client_build":"0.1.0"}"""), Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("unavailable", (await ReadAsync(response)).GetProperty("error").GetString());
        }
    }

    /// <summary>A token service double: returns <see cref="Next"/>, refuses with <see cref="Refuse"/>, or fails as having no issuer when <see cref="Unavailable"/> is set.</summary>
    private sealed class FakeTokenService : ITokenService
    {
        public TokenPair? Next { get; init; }

        public TokenRefreshFailure? Refuse { get; init; }

        public bool Unavailable { get; init; }

        public string? LastRefresh { get; private set; }

        public DeviceInfo? LastDevice { get; private set; }

        public Task<TokenPair> RefreshAsync(string refresh, DeviceInfo device, CancellationToken ct = default)
        {
            LastRefresh = refresh;
            LastDevice = device;
            if (Unavailable) throw new AccessTokenIssuerUnavailableException();
            if (Refuse is { } reason) throw new TokenRefreshException(reason);
            return Task.FromResult(Next ?? throw new InvalidOperationException("No pair configured."));
        }

        public Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
