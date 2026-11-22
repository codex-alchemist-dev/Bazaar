using System.Net;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class StatusEndpointsTests
{
    private sealed class FakeCheck(bool ok) : IReadinessCheck
    {
        public string Name => "fake";

        public Task<ReadinessResult> CheckAsync(CancellationToken ct) => Task.FromResult(new ReadinessResult(Name, ok, ok ? null : "down"));
    }

    private static async Task<(WebApplication App, HttpClient Client, HostState State)> StartAsync(bool checkOk = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var state = new HostState(TimeProvider.System);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new ProviderCapabilityReport([]));
        builder.Services.AddSingleton<IReadinessCheck>(new FakeCheck(checkOk));
        var app = builder.Build();
        app.UseBazaarGetOnly();
        app.MapBazaarStatus();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient(), state);
    }

    [Fact]
    public async Task Writes_are_refused_on_the_status_page_but_allowed_on_api_routes()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            var status = await client.PostAsync("/health/live", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, status.StatusCode);

            var api = await client.PostAsync("/blobs", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, api.StatusCode);

            var tiles = await client.PostAsync("/constellation/tiles", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, tiles.StatusCode);

            var decision = await client.PostAsync("/community/drafts/11111111-1111-1111-1111-111111111111/decision", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, decision.StatusCode);
            var memory = await client.PostAsync("/company-memory/decisions", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, memory.StatusCode);

            var pages = await client.PostAsync("/company-memory/pages", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, pages.StatusCode);

            var delete = await client.DeleteAsync("/blobs", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        }
    }

    [Fact]
    public async Task Admin_panel_paths_skip_the_get_only_gate_and_keep_their_own_method_table()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            // No admin route is mapped in this host, so the request reaches routing (404), not the 405 of the status gate.
            var post = await client.PostAsync("/admin/api/signout", new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
    }

    [Fact]
    public async Task Live_is_ok_even_before_startup_finishes()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Ready_is_503_until_started_then_200()
    {
        var (app, client, state) = await StartAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
            state.MarkStarted();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task Ready_is_503_when_a_check_fails()
    {
        var (app, client, state) = await StartAsync(checkOk: false);
        await using (app)
        {
            state.MarkStarted();
            var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("down", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Status_reports_version_and_uptime()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            var body = await client.GetStringAsync("/status", TestContext.Current.CancellationToken);
            Assert.Contains("uptime_seconds", body, StringComparison.Ordinal);
            Assert.Contains("version", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Healthz_does_not_exist()
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Writes_are_rejected_with_405(string method)
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/status"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/auth/refresh")]
    [InlineData("/auth/handoff")]
    [InlineData("/auth/signout")]
    public async Task Refresh_path_accepts_post(string path)
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            // No route is mapped in this test app, so the guard lets POST through and routing answers 404.
            var response = await client.PostAsync(path, new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("PATCH", "/auth/refresh")]
    [InlineData("GET", "/auth/refresh")]
    [InlineData("GET", "/auth/handoff")]
    [InlineData("PATCH", "/auth/handoff")]
    [InlineData("PATCH", "/auth/signout")]
    [InlineData("GET", "/auth/signout")]
    [InlineData("POST", "/status")]
    [InlineData("PUT", "/status")]
    public async Task Other_methods_on_refresh_and_status_paths_are_405(string method, string path)
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/setup/plans")]
    [InlineData("/setup/plans/abc/apply")]
    public async Task Setup_provisioning_paths_accept_post(string path)
    {
        var (app, client, _) = await StartAsync();
        await using (app)
        {
            // The setup component is not mapped in this test app, so the guard lets POST through and routing answers 404.
            var response = await client.PostAsync(path, new StringContent(string.Empty), TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [Fact]
    public async Task Every_registered_route_is_get_only()
    {
        var (app, _, _) = await StartAsync();
        await using (app)
        {
            var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
            Assert.NotEmpty(endpoints);
            foreach (var endpoint in endpoints)
            {
                var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
                Assert.Equal(["GET"], methods);
            }
        }
    }
}
