using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Git;
using Bazaar.Server.Git.Merge;
using Bazaar.Server.Security.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git.Tests.Merge;

/// <summary>Real Kestrel on a loopback port, as for the read routes. The gate is the real one over fakes, so refusals are checked end to end.</summary>
public sealed class GitMergeRoutesTests : IAsyncLifetime
{
    private const string _url = "/git/fake/repos/acme/widgets/prs/7/merge";

    private readonly FakeForge _forge = new();
    private readonly FakeExecutor _exec = new();
    private readonly FakeVerifier _verifier = new();
    private readonly FakeAudit _audit = new();
    private readonly CapturingLogger _log = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_log);
        builder.Services.AddSingleton<IForgeProvider>(_forge);
        builder.Services.AddSingleton<IForgeMergeExecutor>(_exec);
        builder.Services.AddSingleton<IOwnerSignatureVerifier>(_verifier);
        builder.Services.AddSingleton<IAuditLog>(_audit);
        builder.Services.AddBazaarGit();

        _app = builder.Build();
        _app.MapBazaarGitMerge(route =>
        {
            _guardCalls++;
            return route;
        });
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static GitMergeRequestDto Body(OwnerMergeApproval? approval = null, MergeRequest? merge = null)
    {
        merge ??= TestData.Merge();
        approval ??= TestData.Approval(merge);
        var p = approval.Payload;
        return new GitMergeRequestDto(merge.HeadSha, merge.CommitTitle, merge.SignOff, new GitOwnerApprovalDto(
            p.Action, p.ApprovalId, Convert.ToBase64String(p.ArgsHash), p.HeadSha, p.HumanId, p.DeviceId, p.IssuedAt, p.ExpiresAt,
            Convert.ToBase64String(p.Nonce), Convert.ToBase64String(approval.Signature)));
    }

    private async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> Post(string path, GitMergeRequestDto body)
    {
        var json = JsonSerializer.Serialize(body, GitReadContractsJsonContext.Default.GitMergeRequestDto);
        return await PostRaw(path, json);
    }

    private async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> PostRaw(string path, string json)
    {
        var response = await _http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response);
    }

    private static string Error(string text) => JsonElement.Parse(text).GetProperty("error").GetString()!;

    [Fact]
    public void Guard_is_applied() => Assert.Equal(1, _guardCalls);

    [Fact]
    public async Task Happy_path_returns_200_and_merges_once()
    {
        var (status, text, _) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TestData.Sha, JsonElement.Parse(text).GetProperty("headSha").GetString());
        Assert.Equal(1, _exec.Calls);
        Assert.Equal(OwnerSignedMergeGate.MergedKind, Assert.Single(_audit.Entries).Kind);
    }

    [Fact]
    public async Task Wrong_action_is_403_before_any_forge_call()
    {
        var (status, text, _) = await Post(_url, Body(TestData.Approval(action: "legal_hold.release")));
        AssertRefused(status, text);
    }

    [Fact]
    public async Task Wrong_args_hash_is_403_before_any_forge_call()
    {
        var (status, text, _) = await Post(_url, Body(TestData.Approval(argsHash: [9, 9, 9])));
        AssertRefused(status, text);
    }

    [Fact]
    public async Task Approval_for_another_head_is_403_before_any_forge_call()
    {
        var (status, text, _) = await Post(_url, Body(TestData.Approval(headSha: TestData.OtherSha)));
        AssertRefused(status, text);
    }

    [Fact]
    public async Task Bad_signature_is_403_before_any_forge_call_and_leaks_nothing()
    {
        _verifier.Verdict = OwnerSignatureVerdict.BadSignature;
        var (status, text, _) = await Post(_url, Body());

        AssertRefused(status, text);
        Assert.DoesNotContain("BadSignature", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad_signature", text, StringComparison.Ordinal);
    }

    private void AssertRefused(HttpStatusCode status, string text)
    {
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("merge_not_approved", Error(text));
        Assert.Equal(0, _forge.HeadCalls);
        Assert.Equal(0, _exec.Calls);
    }

    [Fact]
    public async Task Moved_head_is_409_with_the_current_sha()
    {
        _forge.Head = TestData.OtherSha;
        var (status, text, _) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(TestData.OtherSha, JsonElement.Parse(text).GetProperty("currentHeadSha").GetString());
        Assert.Equal(0, _exec.Calls);
    }

    [Fact]
    public async Task Failed_check_is_403()
    {
        _forge.Checks = [new CheckRun("ci", CheckState.Failure, TestData.Sha, null)];
        var (status, text, _) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("merge_not_approved", Error(text));
        Assert.Equal(0, _exec.Calls);
    }

    [Fact]
    public async Task Unknown_provider_is_404()
    {
        // The args hash binds the provider, so the approval is for "nope" and only the registry lookup can refuse it.
        var merge = TestData.Merge();
        var hash = GitMergeArgs.Hash(new PrRef("nope", "acme/widgets", 7), merge);
        var (status, text, _) = await Post("/git/nope/repos/acme/widgets/prs/7/merge", Body(TestData.Approval(merge, argsHash: hash), merge));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("unknown_provider", Error(text));
    }

    [Fact]
    public async Task Unknown_repo_or_pr_is_404()
    {
        _forge.HeadThrows = new ForgeNotFoundException("no such pr");
        var (status, text, _) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", Error(text));
        Assert.Equal(0, _exec.Calls);
    }

    [Fact]
    public async Task Forge_outage_is_502_and_the_message_is_not_echoed_or_logged()
    {
        _exec.Throws = new HttpRequestException("https://forge.example/secret-token-123 exploded");
        var (status, text, _) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("forge_unavailable", Error(text));
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", _log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AQID", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_is_429_with_retry_after()
    {
        _forge.HeadThrows = new ForgeRateLimitedException(TimeSpan.FromSeconds(12));
        var (status, text, response) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(12, JsonElement.Parse(text).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("12", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Rate_limit_without_a_hint_is_429_without_retry_after()
    {
        _forge.HeadThrows = new ForgeRateLimitedException(null);
        var (status, text, response) = await Post(_url, Body());

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.False(JsonElement.Parse(text).TryGetProperty("retryAfterSeconds", out _));
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Theory]
    [InlineData("/git/fake/repos/acme/widgets/prs/0/merge")]
    [InlineData("/git/fake/repos/acme/widgets/prs/abc/merge")]
    [InlineData("/git/Fake!/repos/acme/widgets/prs/7/merge")]
    [InlineData("/git/fake/repos/.acme/widgets/prs/7/merge")]
    public async Task Bad_path_is_400_and_never_reaches_the_gate(string path)
    {
        var (status, text, _) = await Post(path, Body());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", Error(text));
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _forge.HeadCalls);
    }

    [Fact]
    public async Task Malformed_bodies_are_400_and_never_reach_the_gate()
    {
        var good = Body();
        var badSha = good with { HeadSha = "nope" };
        var badBase64 = good with { Approval = good.Approval with { SignatureBase64 = "!!!" } };
        var noTitle = good with { CommitTitle = " " };
        var huge = good with { SignOff = new string('x', 5000) };

        foreach (var body in new[] { badSha, badBase64, noTitle, huge })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(_url, body)).Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(_url, "{not json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(_url, "{}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(_url, "null")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(_url, new string(' ', 20_000))).Status);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _forge.HeadCalls);
    }

    [Fact]
    public async Task Signature_is_never_logged()
    {
        _verifier.Verdict = OwnerSignatureVerdict.BadSignature;
        await Post(_url, Body());
        Assert.DoesNotContain("AQID", _log.Text, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) => new Sink(_lines);

        public void Dispose()
        {
        }

        private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + exception);
        }
    }
}
