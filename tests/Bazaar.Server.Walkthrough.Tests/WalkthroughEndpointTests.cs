using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Walkthrough.Tests;

public class WalkthroughEndpointTests
{
    private sealed class Resolver : IWalkthroughCallerResolver
    {
        public Task<string?> ResolveAsync(string? header, CancellationToken cancellationToken = default) =>
            Task.FromResult(header switch { "Bearer alice" => "alice", "Bearer bob" => "bob", _ => (string?)null });
    }

    private sealed class Facts : IWalkthroughFactsProvider
    {
        public Task<WalkthroughServerFacts> ReadAsync(string personId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WalkthroughServerFacts(true, personId == "alice" ? "Claude" : null, false));
    }

    private static async Task<(HttpClient Client, IAsyncDisposable Host)> StartAsync(bool withPorts = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (withPorts)
        {
            builder.Services.AddSingleton<IWalkthroughCallerResolver, Resolver>();
            builder.Services.AddSingleton<IWalkthroughFactsProvider, Facts>();
            builder.Services.AddSingleton<IWalkthroughPrefsStore, InMemoryWalkthroughPrefsStore>();
        }

        var app = builder.Build();
        app.MapWalkthroughEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string? token = "alice", string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public async Task Facts_answer_for_the_signed_in_person_and_a_stranger_gets_401()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;

        var ok = await client.SendAsync(Req(HttpMethod.Get, "/onboarding/walkthrough/facts"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var text = await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"first_agent_name\":\"Claude\"", text);
        Assert.Contains("\"company_created\":true", text);

        var refused = await client.SendAsync(Req(HttpMethod.Get, "/onboarding/walkthrough/facts", token: "nobody"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var noToken = await client.SendAsync(Req(HttpMethod.Get, "/prefs/walkthrough", token: null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task Prefs_round_trip_per_person_and_start_as_404()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, "/prefs/walkthrough"), ct)).StatusCode);

        var put = await client.SendAsync(Req(HttpMethod.Put, "/prefs/walkthrough", body: """{"dismissed":true,"finished":false,"current":"MeetAgent","completed":["CreateCompany","CreateCompany"]}"""), ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var got = await client.SendAsync(Req(HttpMethod.Get, "/prefs/walkthrough"), ct);
        var text = await got.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"current\":\"MeetAgent\"", text);
        Assert.Contains("\"completed\":[\"CreateCompany\"]", text);

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, "/prefs/walkthrough", token: "bob"), ct)).StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"dismissed":false,"finished":false,"current":"Bogus","completed":[]}""")]
    [InlineData("""{"dismissed":false,"finished":false,"current":"Done","completed":["Nope"]}""")]
    [InlineData("""{"dismissed":false,"finished":false,"completed":[]}""")]
    public async Task A_body_with_unknown_or_missing_steps_is_refused_and_stores_nothing(string body)
    {
        var (client, host) = await StartAsync();
        await using var _ = host;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Put, "/prefs/walkthrough", body: body), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, "/prefs/walkthrough"), ct)).StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;
        var big = """{"dismissed":false,"finished":false,"current":"Done","completed":[""" + string.Join(",", Enumerable.Repeat("\"Done\"", 600)) + "]}";

        var reply = await client.SendAsync(Req(HttpMethod.Put, "/prefs/walkthrough", body: big), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.StatusCode);
    }

    [Fact]
    public async Task Routes_answer_503_until_their_ports_are_registered()
    {
        var (client, host) = await StartAsync(withPorts: false);
        await using var _ = host;

        var reply = await client.SendAsync(Req(HttpMethod.Get, "/onboarding/walkthrough/facts"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
    }
}

public class WalkthroughComponentTests
{
    [Fact]
    public async Task The_component_maps_the_routes_only_when_enabled()
    {
        foreach (var enabled in new[] { false, true })
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            if (enabled) new WalkthroughComponent().AddServices(builder.Services, builder.Configuration);
            var app = builder.Build();
            app.MapBazaarWalkthrough();
            await app.StartAsync(TestContext.Current.CancellationToken);
            await using (app)
            {
                var reply = await app.GetTestClient().GetAsync("/prefs/walkthrough", TestContext.Current.CancellationToken);
                // Enabled with no resolver registered: 503. Not enabled: no route, 404.
                Assert.Equal(enabled ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound, reply.StatusCode);
            }
        }
    }
}
