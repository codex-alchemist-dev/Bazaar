using System.Net;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Community.GitHub.Bridge.Tests;

public sealed class BridgeTests
{
    private const string _secret = "ghs_SECRETTOKEN123";

    private static readonly CommunityPullRequestRequest _request =
        new(Guid.NewGuid(), "acme", "widgets", "Fix crash", "Body <!-- bazaar-draft:x -->", "agent/fix", "main");

    [Fact]
    public async Task Token_source_asks_for_the_resolved_installation_and_repo()
    {
        var provider = new FakeTokenProvider();
        var source = new BridgeCommunityGitHubTokenSource(provider, new FakeResolver(new CommunityInstallation(77, 5)));

        var token = await source.GetInstallationTokenAsync("acme", "widgets", CancellationToken.None);

        Assert.Equal(_secret, token);
        var scope = Assert.Single(provider.Scopes);
        Assert.Equal(77, scope.InstallationId);
        Assert.Equal([5L], scope.RepositoryIds);
        Assert.Equal("write", scope.Permissions["issues"]);
        Assert.Equal("write", scope.Permissions["pull_requests"]);
    }

    [Fact]
    public async Task Token_source_refuses_when_not_installed()
    {
        var source = new BridgeCommunityGitHubTokenSource(new FakeTokenProvider(), new FakeResolver(null));
        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => source.GetInstallationTokenAsync("acme", "widgets", CancellationToken.None));
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(500, true)]
    [InlineData(429, true)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task Token_failures_map_retryable_by_status(int? status, bool retryable)
    {
        var provider = new FakeTokenProvider { Failure = new GitHubTokenException($"exchange failed {_secret}", status) };
        var source = new BridgeCommunityGitHubTokenSource(provider, new FakeResolver(new CommunityInstallation(1, 2)));

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => source.GetInstallationTokenAsync("acme", "widgets", CancellationToken.None));

        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain(_secret, ex.Message);
    }

    [Fact]
    public async Task Opener_maps_request_and_result()
    {
        var adapter = new FakeAdapter();
        var opener = new BridgeCommunityPullRequestOpener(adapter, new FakeResolver(new CommunityInstallation(77, 5)));

        var receipt = await opener.OpenDraftAsync(_request, CancellationToken.None);

        var sent = Assert.Single(adapter.Requests);
        Assert.Equal(new OpenPrRequest(77, "acme", "widgets", "agent/fix", "main", "Fix crash", _request.Body), sent);
        Assert.Equal("PR_node", receipt.ExternalId);
        Assert.Equal(new Uri("https://github.com/acme/widgets/pull/43"), receipt.Url);
        Assert.False(receipt.Reused);
    }

    [Fact]
    public async Task Opener_refuses_when_not_installed()
    {
        var opener = new BridgeCommunityPullRequestOpener(new FakeAdapter(), new FakeResolver(null));
        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => opener.OpenDraftAsync(_request, CancellationToken.None));
        Assert.False(ex.Retryable);
    }

    public static TheoryData<Exception, bool> OpenFailures() => new()
    {
        { new GitHubApiException(HttpStatusCode.UnprocessableEntity, "bad", $"x {_secret}"), false },
        { new GitHubApiException(HttpStatusCode.Forbidden, "no", "x"), false },
        { new GitHubApiException(HttpStatusCode.BadGateway, null, "x"), true },
        { new GitHubApiException(null, null, "x"), true },
        { new GitHubRateLimitedException(1, DateTimeOffset.UtcNow, HttpStatusCode.Forbidden, "limited"), true },
        { new GitHubTokenException("t", 503), true },
        { new GitHubTokenException("t", 403), false },
        { new HttpRequestException("down"), true },
        { new TimeoutException(), true },
    };

    [Theory]
    [MemberData(nameof(OpenFailures))]
    public async Task Open_failures_map_retryable_and_never_leak_token(Exception failure, bool retryable)
    {
        var opener = new BridgeCommunityPullRequestOpener(new FakeAdapter { Failure = failure }, new FakeResolver(new CommunityInstallation(1, 2)));

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => opener.OpenDraftAsync(_request, CancellationToken.None));

        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain(_secret, ex.Message);
    }

    [Fact]
    public async Task Cancellation_passes_through()
    {
        var opener = new BridgeCommunityPullRequestOpener(new FakeAdapter { Failure = new OperationCanceledException() }, new FakeResolver(new CommunityInstallation(1, 2)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => opener.OpenDraftAsync(_request, CancellationToken.None));
    }

    [Fact]
    public void Registration_uses_TryAdd()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitHubTokenProvider>(new FakeTokenProvider());
        services.AddSingleton<IGitHubAdapter>(new FakeAdapter());
        services.AddSingleton<ICommunityInstallationResolver>(new FakeResolver(null));
        var custom = new CustomOpener();
        services.AddSingleton<ICommunityPullRequestOpener>(custom);

        services.AddBazaarCommunityGitHubBridge();
        services.AddBazaarCommunityGitHubBridge();

        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<ICommunityPullRequestOpener>());
        Assert.IsType<BridgeCommunityGitHubTokenSource>(provider.GetRequiredService<ICommunityGitHubTokenSource>());
        Assert.Single(services, d => d.ServiceType == typeof(ICommunityGitHubTokenSource));
    }

    private sealed class CustomOpener : ICommunityPullRequestOpener
    {
        public Task<PostReceipt> OpenDraftAsync(CommunityPullRequestRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeResolver(CommunityInstallation? result) : ICommunityInstallationResolver
    {
        public Task<CommunityInstallation?> ResolveAsync(string owner, string repo, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeTokenProvider : IGitHubTokenProvider
    {
        public List<GitHubTokenScope> Scopes { get; } = [];

        public Exception? Failure { get; init; }

        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
        {
            Scopes.Add(scope);
            return Failure is null
                ? Task.FromResult(new GitHubToken(_secret, DateTimeOffset.UtcNow.AddHours(1)))
                : Task.FromException<GitHubToken>(Failure);
        }
    }

    private sealed class FakeAdapter : IGitHubAdapter
    {
        public List<OpenPrRequest> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Failure is null
                ? Task.FromResult(new PullRequestRef(new PrRef(request.InstallationId, request.Owner, request.Repo, 43), "PR_node", "abc123", true))
                : Task.FromException<PullRequestRef>(Failure);
        }

        public Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
