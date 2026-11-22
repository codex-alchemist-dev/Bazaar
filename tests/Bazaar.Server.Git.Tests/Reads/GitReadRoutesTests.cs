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
    private readonly FakeForge _gitlab = new("gitlab");
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IForgeProvider>(_forge);
        builder.Services.AddSingleton<IForgeProvider>(_gitlab);
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
    public void Guard_is_applied_to_every_route() => Assert.Equal(6, _guardCalls);

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

    private const string _subgroup = "/git/gitlab/repos/group/sub/sub2/project";

    [Fact]
    public async Task Subgroup_list_passes_the_full_path_to_the_forge()
    {
        var (status, body) = await Get($"{_subgroup}/prs?state=open&page=2&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("open", body.GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Equal(new RepoRef("gitlab", "group/sub/sub2/project"), _gitlab.LastRepo);
        Assert.Equal(new ListPrsQuery(PrState.Open, 2, 5), _gitlab.LastQuery);
    }

    [Fact]
    public async Task Subgroup_detail_files_and_checks_pass_the_full_path_and_number()
    {
        var expected = new PrRef("gitlab", "group/sub/sub2/project", 7);

        Assert.Equal(HttpStatusCode.OK, (await Get($"{_subgroup}/prs/7")).Status);
        Assert.Equal(expected, _gitlab.LastPr);

        Assert.Equal(HttpStatusCode.OK, (await Get($"{_subgroup}/prs/7/files?head={TestData.Sha}")).Status);
        Assert.Equal(expected, _gitlab.LastPr);

        var (status, body) = await Get($"{_subgroup}/prs/7/checks");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("success", body[0].GetProperty("state").GetString());
        Assert.Equal(expected, _gitlab.LastPr);
    }

    [Fact]
    public async Task A_one_segment_owner_on_gitlab_still_uses_the_fixed_routes()
    {
        Assert.Equal(HttpStatusCode.OK, (await Get("/git/gitlab/repos/group/project/prs")).Status);
        Assert.Equal(new RepoRef("gitlab", "group/project"), _gitlab.LastRepo);
    }

    [Fact]
    public async Task An_encoded_slash_is_not_a_separator_so_the_path_is_404()
    {
        // Kestrel keeps %2F inside one segment, so the project id arrives as one unknown segment and no route matches.
        var (status, _) = await Get("/git/gitlab/repos/group%2Fsub%2Fproject/prs/3");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Null(_gitlab.LastPr);
    }

    [Fact]
    public async Task A_project_named_prs_resolves_to_its_own_repo()
    {
        var (status, _) = await Get("/git/gitlab/repos/group/prs/prs");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new RepoRef("gitlab", "group/prs"), _gitlab.LastRepo);
    }

    [Fact]
    public async Task A_repo_with_the_maximum_segments_is_accepted_and_one_more_is_400()
    {
        var atLimit = string.Join('/', Enumerable.Repeat("g", 20)) + "/project";
        var overLimit = string.Join('/', Enumerable.Repeat("g", 21)) + "/project";

        Assert.Equal(HttpStatusCode.OK, (await Get($"/git/gitlab/repos/{atLimit}/prs")).Status);

        var (status, body) = await Get($"/git/gitlab/repos/{overLimit}/prs");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_repo", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/git/gitlab/repos/group/sub/project")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/abc")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/7/comments")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/abc/files")]
    [InlineData("/git/gitlab/repos/solo/prs")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/7/files/extra")]
    public async Task Malformed_nested_paths_are_404(string path)
    {
        var (status, _) = await Get(path);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/0")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/9999999999999999999999")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/7/files")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs/7/files?head=abc")]
    [InlineData("/git/gitlab/repos/group/sub/-bad/project/prs")]
    [InlineData("/git/gitlab/repos/group//sub/project/prs")]
    [InlineData("/git/gitlab/repos/group/sub/wid%20gets/prs")]
    [InlineData("/git/gitlab/repos/group/sub/project/prs?state=bogus")]
    public async Task Malformed_subgroup_input_is_400(string path)
    {
        var (status, _) = await Get(path);
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Theory]
    [InlineData("/git/fake/repos/acme/sub/widgets/prs")]
    [InlineData("/git/fake/repos/acme/sub/widgets/prs/7")]
    [InlineData("/git/fake/repos/acme/sub/widgets/prs/7/checks")]
    public async Task Github_and_other_providers_keep_one_segment_owners(string path)
    {
        var (status, _) = await Get(path);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Null(_forge.LastRepo);
        Assert.Null(_forge.LastPr);
    }
}
