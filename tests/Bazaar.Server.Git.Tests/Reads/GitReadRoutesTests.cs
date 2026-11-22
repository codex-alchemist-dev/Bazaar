using System.Net;
using System.Text.Json;
using Bazaar.Server.Git.Forge;
using Bazaar.Server.Git.Reads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git.Tests.Reads;

/// <summary>Real Kestrel on a loopback port, so the routing and the status codes are what a client sees. No test-host package needed.</summary>
public sealed class GitReadRoutesTests : IAsyncLifetime
{
    private readonly FakeForge _forge = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IForgeProvider>(_forge);
        builder.Services.AddBazaarGit();
        builder.Services.AddSingleton<IForgeMergeExecutor>(new FakeExecutor());

        _app = builder.Build();
        _app.MapBazaarGitReads(route =>
        {
            _guardCalls++;
            return route;
        });
        await _app.StartAsync();

        var address = _app.Urls.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path)
    {
        using var response = await _http.GetAsync(path, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private const string _base = "/git/fake/repos/acme/widgets/prs";

    [Fact]
    public void Guard_is_applied_to_every_route() => Assert.Equal(4, _guardCalls);

    [Fact]
    public async Task List_returns_200_with_camel_case_json()
    {
        var (status, body) = await Get(_base);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("stale").GetBoolean());
        Assert.Equal("open", body.GetProperty("items")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Detail_files_and_checks_return_200()
    {
        Assert.Equal(HttpStatusCode.OK, (await Get($"{_base}/7")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get($"{_base}/7/files?head={TestData.Sha}")).Status);
        var (status, body) = await Get($"{_base}/7/checks");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("success", body[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unknown_provider_is_404()
    {
        var (status, body) = await Get("/git/nope/repos/acme/widgets/prs");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("unknown_provider", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Moved_head_is_409_with_the_current_sha()
    {
        _forge.Head = TestData.OtherSha;

        var (status, body) = await Get($"{_base}/7/files?head={TestData.Sha}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(TestData.OtherSha, body.GetProperty("currentHeadSha").GetString());
    }

    [Fact]
    public async Task Forge_failure_is_502_without_leaking_the_message()
    {
        _forge.OnList = _ => throw new HttpRequestException("secret-token-xyz in url");

        using var response = await _http.GetAsync(_base, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_repo_or_pr_is_404_not_found()
    {
        _forge.OnList = _ => throw new ForgeNotFoundException("no such repo");

        var (status, body) = await Get(_base);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Rate_limit_is_429_with_retry_after()
    {
        _forge.OnList = _ => throw new ForgeRateLimitedException(TimeSpan.FromSeconds(12));

        using var response = await _http.GetAsync(_base, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("12", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("rate_limited", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(12, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task Rate_limit_without_a_hint_is_429_without_retry_after()
    {
        _forge.OnList = _ => throw new ForgeRateLimitedException(null);

        using var response = await _http.GetAsync(_base, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Theory]
    [InlineData("/git/fake/repos/acme/widgets/prs/0")]
    [InlineData("/git/fake/repos/acme/widgets/prs/-3")]
    [InlineData("/git/fake/repos/acme/widgets/prs/abc")]
    [InlineData("/git/fake/repos/acme/widgets/prs/1/files")]
    [InlineData("/git/fake/repos/acme/widgets/prs/1/files?head=abc")]
    [InlineData("/git/fake/repos/acme/widgets/prs/1/files?head=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("/git/fake/repos/acme/widgets/prs?state=bogus")]
    [InlineData("/git/fake/repos/acme/widgets/prs?page=0")]
    [InlineData("/git/fake/repos/acme/widgets/prs?pageSize=-1")]
    [InlineData("/git/fake/repos/acme/-bad/prs")]
    [InlineData("/git/fake/repos/acme/wid%20gets/prs")]
    [InlineData("/git/FAKE/repos/acme/widgets/prs")]
    [InlineData("/git/fake/repos/acme/widgets/prs/9999999999999999999999")]
    public async Task Bad_input_is_400(string path)
    {
        var (status, _) = await Get(path);
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
