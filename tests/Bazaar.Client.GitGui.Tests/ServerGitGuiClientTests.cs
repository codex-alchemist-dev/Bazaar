using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Git;

// These tests use an in-memory fake transport with no real waiting; a test-runner cancellation token adds nothing here.
#pragma warning disable xUnit1051

namespace Bazaar.Client.GitGui.Tests;

public sealed class ServerGitGuiClientTests
{
    private sealed class FakeTransport(Func<string, GitHttpResponse> answer) : IGitHttpTransport
    {
        public List<string> Paths { get; } = [];

        public List<(string Path, byte[] Body)> Posts { get; } = [];

        public Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Posts.Add((path, jsonBody));
            return Task.FromResult(answer(path));
        }

        public Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(pathAndQuery);
            return Task.FromResult(answer(pathAndQuery));
        }
    }

    private static GitHttpResponse Json(int status, string json) => new(status, Encoding.UTF8.GetBytes(json));

    private static (ServerGitGuiClient Client, FakeTransport Transport) Make(GitHttpResponse response)
    {
        var t = new FakeTransport(_ => response);
        return (new ServerGitGuiClient(t), t);
    }

    [Fact]
    public async Task ServerRepoSource_maps_the_repos_route_to_rows()
    {
        var (client, transport) = Make(Json(200, """[{"provider":"github","fullName":"acme/widgets","defaultBranch":"main"},{"provider":"gitlab","fullName":"g/p"}]"""));
        var source = new ServerRepoSource(client);

        var rows = await source.ListReposAsync();

        Assert.Equal(["git/repos"], transport.Paths);
        Assert.Equal([new GitRepoRow("github", "acme/widgets", "main"), new GitRepoRow("gitlab", "g/p", "")], rows);
        Assert.Empty(await source.ListBranchesAsync(rows[0]));
        Assert.Empty(await source.ListCommitsAsync(rows[0], "main"));
    }

    [Fact]
    public async Task ListRepos_maps_failures_like_the_other_reads()
    {
        var (unavailable, _) = Make(Json(502, """{"error":"forge_unavailable"}"""));
        await Assert.ThrowsAsync<GitServerUnavailableException>(() => unavailable.ListReposAsync());
        var (bad, _) = Make(Json(200, "not json"));
        await Assert.ThrowsAsync<GitResponseException>(() => bad.ListReposAsync());
    }

    [Fact]
    public async Task InMemory_client_lists_its_repos()
    {
        var fake = new InMemoryGitGuiClient();
        fake.Repos.Add(new GitRepoDto("github", "a/b", null));

        Assert.Single(await fake.ListReposAsync());
    }

    [Fact]
    public async Task ListPrs_builds_url_and_returns_dto()
    {
        var page = new GitPrPageDto([Fixtures.Summary(1)], true, false);
        var (client, t) = Make(Json(200, JsonSerializer.Serialize(page, GitReadContractsJsonContext.Default.GitPrPageDto)));

        var result = await client.ListPrsAsync("github", "acme/widgets", "open", 2, 30);

        Assert.Equal("git/github/repos/acme/widgets/prs?state=open&page=2&pageSize=30", Assert.Single(t.Paths));
        Assert.True(result.HasMore);
        Assert.Equal(1, Assert.Single(result.Items).Number);
    }

    [Fact]
    public async Task GetPr_files_and_checks_use_the_route_shapes()
    {
        var detail = Fixtures.Detail(Fixtures.Summary(7));
        var t = new FakeTransport(p => p.EndsWith("/files?head=" + Fixtures.Head1, StringComparison.Ordinal)
            ? Json(200, JsonSerializer.Serialize(Fixtures.Files, GitReadContractsJsonContext.Default.GitPrFileDtoArray))
            : p.EndsWith("/checks", StringComparison.Ordinal)
                ? Json(200, JsonSerializer.Serialize(new[] { Fixtures.Check("success") }, GitReadContractsJsonContext.Default.GitCheckDtoArray))
                : Json(200, JsonSerializer.Serialize(detail, GitReadContractsJsonContext.Default.GitPrDetailDto)));
        var client = new ServerGitGuiClient(t);

        Assert.Equal(7, (await client.GetPrAsync("gitlab", "grp/proj", 7)).Summary.Number);
        Assert.Equal(2, (await client.GetFilesAsync("gitlab", "grp/proj", 7, Fixtures.Head1)).Count);
        Assert.Single(await client.GetChecksAsync("gitlab", "grp/proj", 7));
        Assert.Equal(
            ["git/gitlab/repos/grp/proj/prs/7", $"git/gitlab/repos/grp/proj/prs/7/files?head={Fixtures.Head1}", "git/gitlab/repos/grp/proj/prs/7/checks"],
            t.Paths);
    }

    [Fact]
    public async Task Conflict_maps_to_head_moved_with_current_sha()
    {
        var (client, _) = Make(Json(409, $$"""{"currentHeadSha":"{{Fixtures.Head2}}"}"""));
        var ex = await Assert.ThrowsAsync<HeadMovedException>(() => client.GetFilesAsync("github", "a/b", 1, Fixtures.Head1));
        Assert.Equal(Fixtures.Head2, ex.CurrentSha);
    }

    [Fact]
    public async Task Conflict_with_unusable_body_is_a_request_error_not_head_moved()
    {
        var (client, _) = Make(Json(409, """{"currentHeadSha":"not-a-sha"}"""));
        await Assert.ThrowsAsync<GitRequestException>(() => client.GetFilesAsync("github", "a/b", 1, Fixtures.Head1));
    }

    [Fact]
    public async Task NotFound_maps_to_typed_not_found_with_code()
    {
        var (client, _) = Make(Json(404, """{"error":"unknown_provider"}"""));
        var ex = await Assert.ThrowsAsync<GitNotFoundException>(() => client.GetPrAsync("github", "a/b", 1));
        Assert.Equal("unknown_provider", ex.ErrorCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Other_4xx_is_a_request_error_with_only_the_short_code(int status)
    {
        var (client, _) = Make(Json(status, """{"error":"bad_state"}"""));
        var ex = await Assert.ThrowsAsync<GitRequestException>(() => client.GetChecksAsync("github", "a/b", 1));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("bad_state", ex.ErrorCode);
    }

    [Fact]
    public async Task Hostile_error_text_is_never_kept()
    {
        var (client, _) = Make(Json(400, """{"error":"<script>alert(1)</script> token=abc"}"""));
        var ex = await Assert.ThrowsAsync<GitRequestException>(() => client.GetChecksAsync("github", "a/b", 1));
        Assert.Equal("unknown", ex.ErrorCode);
        Assert.DoesNotContain("script", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Server_errors_map_to_unavailable(int status)
    {
        var (client, _) = Make(Json(status, """{"error":"forge_unavailable"}"""));
        await Assert.ThrowsAsync<GitServerUnavailableException>(() => client.GetPrAsync("github", "a/b", 1));
    }

    [Fact]
    public async Task Connection_failure_maps_to_unavailable()
    {
        var client = new ServerGitGuiClient(new ThrowingTransport(new HttpRequestException("secret host detail")));
        var ex = await Assert.ThrowsAsync<GitServerUnavailableException>(() => client.GetPrAsync("github", "a/b", 1));
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(302)]
    public async Task Unexpected_success_or_redirect_status_is_a_response_error(int status)
    {
        var (client, _) = Make(new GitHttpResponse(status, []));
        await Assert.ThrowsAsync<GitResponseException>(() => client.GetPrAsync("github", "a/b", 1));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public async Task Malformed_json_is_a_typed_error(string body)
    {
        var (client, _) = Make(Json(200, body));
        await Assert.ThrowsAsync<GitResponseException>(() => client.GetPrAsync("github", "a/b", 1));
        await Assert.ThrowsAsync<GitResponseException>(() => client.ListPrsAsync("github", "a/b", "all", 1, 10));
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        var (client, _) = Make(new GitHttpResponse(200, new byte[ServerGitGuiClient.MaxBodyBytes + 1]));
        await Assert.ThrowsAsync<GitResponseException>(() => client.GetChecksAsync("github", "a/b", 1));
    }

    [Theory]
    [InlineData("GitHub", "a/b")]
    [InlineData("github", "a")]
    [InlineData("github", "a/b/c")]
    [InlineData("github", "../etc")]
    [InlineData("github", "a/..")]
    [InlineData("github", "a/b%2F..")]
    [InlineData("github", "a/b?x=1")]
    [InlineData("github", "a/b#frag")]
    [InlineData("github", "a b/c")]
    [InlineData("github", "a/b\r\nX: y")]
    [InlineData("github/../x", "a/b")]
    [InlineData("", "a/b")]
    [InlineData("github", "")]
    [InlineData("github", ".hidden/b")]
    public async Task Invalid_segments_never_reach_the_transport(string provider, string repo)
    {
        var (client, t) = Make(Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetPrAsync(provider, repo, 1));
        Assert.Empty(t.Paths);
    }

    [Theory]
    [InlineData("open&admin=1")]
    [InlineData("OPEN")]
    [InlineData("")]
    public async Task Invalid_state_is_refused_locally(string state)
    {
        var (client, t) = Make(Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListPrsAsync("github", "a/b", state, 1, 10));
        Assert.Empty(t.Paths);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1111111111111111111111111111111111111111&x=1")]
    [InlineData("zzzz111111111111111111111111111111111111")]
    public async Task Invalid_head_is_refused_locally(string head)
    {
        var (client, t) = Make(Json(200, "[]"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFilesAsync("github", "a/b", 1, head));
        Assert.Empty(t.Paths);
    }

    [Fact]
    public async Task Bad_numbers_are_refused_locally()
    {
        var (client, t) = Make(Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetPrAsync("github", "a/b", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListPrsAsync("github", "a/b", "open", 0, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListPrsAsync("github", "a/b", "open", 1, 0));
        Assert.Empty(t.Paths);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var (client, t) = Make(Json(200, "{}"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPrAsync("github", "a/b", 1, cts.Token));
        Assert.Empty(t.Paths);
    }

    [Fact]
    public async Task Http_transport_sends_bearer_per_request_and_returns_status_and_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"x"}""") });
        var token = "t1";
        var transport = new HttpGitTransport(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") }, () => token);

        var r1 = await transport.GetAsync("git/github/repos/a/b/prs", 1024);
        token = "t2";
        await transport.GetAsync("git/github/repos/a/b/prs", 1024);

        Assert.Equal(404, r1.StatusCode);
        Assert.Equal(["Bearer t1", "Bearer t2"], handler.Auth);
        Assert.Equal("/git/github/repos/a/b/prs", handler.Paths[0]);
    }

    [Fact]
    public async Task Http_transport_refuses_a_body_over_the_cap()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) });
        var transport = new HttpGitTransport(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") }, () => null);

        await Assert.ThrowsAsync<GitResponseException>(() => transport.GetAsync("git/x", 1000));
        Assert.Equal([null], handler.Auth);
    }

    private sealed class ThrowingTransport(Exception ex) : IGitHttpTransport
    {
        public Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default) => throw ex;

        public Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default) => throw ex;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string?> Auth { get; } = [];

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Auth.Add(request.Headers.Authorization?.ToString());
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(answer(request));
        }
    }
}
