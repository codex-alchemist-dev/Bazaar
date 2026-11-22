using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SoulProfile.Tests;

public class SoulProfileEndpointTests
{
    private static readonly Guid Known = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SoulId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private sealed class Resolver : ISoulProfileCallerResolver
    {
        public Task<string?> ResolveAsync(string? header, CancellationToken cancellationToken = default) =>
            Task.FromResult(header switch { "Bearer owner" => "owner", "Bearer guest" => "guest", _ => (string?)null });
    }

    private sealed class Source : ISoulProfileSource
    {
        public Task<AgentProfileInfo?> ReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(personId == "owner" && agentId == Known
                ? new AgentProfileInfo(SoulId, "Claude", "Developer", "Haiku 5.5",
                    [new SoulVersionInfo(1, null, "aa", "{\"tone\":\"calm\"}", "bb", "owner", "first", null, DateTimeOffset.UnixEpoch)])
                : null);
    }

    private static async Task<(HttpClient Client, IAsyncDisposable Host)> StartAsync(bool ports = true, bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (enabled) new SoulProfileComponent().AddServices(builder.Services, builder.Configuration);
        if (ports)
        {
            builder.Services.AddSingleton<ISoulProfileCallerResolver, Resolver>();
            builder.Services.AddSingleton<ISoulProfileSource, Source>();
        }

        var app = builder.Build();
        app.MapBazaarSoulProfile();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    private static HttpRequestMessage Get(string path, string? token = "owner")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task The_owner_reads_the_profile_with_snake_case_keys_and_no_charter_text()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;

        var reply = await client.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken);
        var text = await reply.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("\"soul_id\":\"" + SoulId + "\"", text);
        Assert.Contains("\"model_badge\":\"Haiku 5.5\"", text);
        Assert.Contains("\"charter_sha256\":\"aa\"", text);
        Assert.DoesNotContain("charter\":", text);
    }

    [Fact]
    public async Task A_stranger_gets_401_and_every_other_miss_gets_the_same_404()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get($"/agents/{Known}/profile", token: null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get($"/agents/{Known}/profile", token: "nobody"), ct)).StatusCode);

        var unknown = await client.SendAsync(Get($"/agents/{Guid.NewGuid()}/profile"), ct);
        var refused = await client.SendAsync(Get($"/agents/{Known}/profile", token: "guest"), ct);
        var malformed = await client.SendAsync(Get("/agents/not-a-guid/profile"), ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(ct), await refused.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task The_route_answers_503_without_its_ports_and_404_when_the_component_is_off()
    {
        var (noPorts, hostA) = await StartAsync(ports: false);
        await using var _a = hostA;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noPorts.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken)).StatusCode);

        var (off, hostB) = await StartAsync(enabled: false);
        await using var _b = hostB;
        Assert.Equal(HttpStatusCode.NotFound, (await off.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken)).StatusCode);
    }
}
