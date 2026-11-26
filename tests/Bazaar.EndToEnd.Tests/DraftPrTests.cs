using Bazaar.EndToEnd.Tests.Harness;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// The draft-PR step of the acceptance path (docs/design/21, "Pull requests"; docs/design/17, tool <c>git_push_draft_pr</c>).
/// The real <see cref="GitHubAdapter"/> opens a draft pull request over https against the stub GitHub. It does not need the server
/// process or a database. The branch push and the host wiring are not covered (GAPS.md, gap 12).
/// </summary>
public sealed class DraftPrTests
{
    private sealed class FixedTokens : IGitHubTokenProvider
    {
        public const string Value = "ghs_e2e_installation_token";

        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubToken(Value, DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    private static GitHubAdapter AdapterFor(StubGitHubServer github) => new(
        new HttpClient(github.CreateTrustingHandler()),
        new FixedTokens(),
        new InMemoryGitHubRateState(),
        new GitHubAdapterOptions { BaseAddress = github.BaseAddress, MaxRetries = 0 },
        NullLogger<GitHubAdapter>.Instance);

    private static OpenPrRequest Request(string branch) =>
        new(11, "owner", "sample", branch, "main", "fix(add): add instead of subtract", "Fixes the failing test.");

    [Fact]
    public async Task The_adapter_opens_a_draft_pull_request_with_the_installation_token()
    {
        await using var github = await StubGitHubServer.StartAsync();

        var pr = await AdapterFor(github).OpenDraftPrAsync(Request("agent/fix-add"), TestContext.Current.CancellationToken);

        Assert.True(pr.Draft);
        Assert.Equal(new PrRef(11, "owner", "sample", pr.Pr.Number), pr.Pr);
        var create = Assert.Single(github.Requests, r => r.Method == "POST");
        Assert.Equal("Bearer " + FixedTokens.Value, create.Authorization);
        Assert.Contains("\"draft\":true", create.Body, StringComparison.Ordinal);
        Assert.Contains("\"head\":\"agent/fix-add\"", create.Body, StringComparison.Ordinal);
        Assert.Contains("\"base\":\"main\"", create.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_the_same_branch_twice_returns_the_same_pull_request()
    {
        await using var github = await StubGitHubServer.StartAsync();
        var adapter = AdapterFor(github);

        var first = await adapter.OpenDraftPrAsync(Request("agent/fix-add"), TestContext.Current.CancellationToken);
        var second = await adapter.OpenDraftPrAsync(Request("agent/fix-add"), TestContext.Current.CancellationToken);

        Assert.Equal(first.Pr, second.Pr);
        Assert.Single(github.Requests, r => r.Method == "POST");
    }
}
