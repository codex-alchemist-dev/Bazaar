using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Community.GitHub.Tests;

internal static class TestSupport
{
    public const string Footer = "Posted by an AI agent (Bazaar), sponsor Sam, contact sam@example.org.";

    public static OutboundPost Post(
        string kind = "issue",
        string? body = null,
        string? target = null,
        Guid? draftId = null,
        string platform = "github")
    {
        body ??= "Found a crash on startup.\n\n" + Footer;
        target ??= kind == "comment" ? "acme/widgets#7" : "acme/widgets";
        return new OutboundPost(
            draftId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            platform,
            kind,
            target,
            kind == "comment" ? null : "Crash on startup",
            body,
            Hash(body),
            kind == "pr" ? "agent/fix" : null,
            kind == "pr" ? "main" : null);
    }

    public static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public static ApprovalId Approval { get; } = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
}

/// <summary>Stands in for the host's binding to Bazaar.GitHub's IGitHubAdapter.OpenDraftPrAsync.</summary>
internal sealed class RecordingPullRequestOpener : ICommunityPullRequestOpener
{
    public List<CommunityPullRequestRequest> Requests { get; } = [];

    public CommunityPostException? Failure { get; set; }

    public Task<PostReceipt> OpenDraftAsync(CommunityPullRequestRequest request, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Requests.Add(request);
        return Task.FromResult(new PostReceipt("3001", new Uri("https://github.com/acme/widgets/pull/43"), Reused: false));
    }
}

internal sealed class FixedTokenSource(string token = "ghs_SECRETTOKEN123") : ICommunityGitHubTokenSource
{
    public Task<string> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken) => Task.FromResult(token);
}

/// <summary>A scripted <see cref="HttpMessageHandler"/>: each request gets the next canned response, and requests are recorded.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<(HttpMethod Method, Uri Uri, string? Authorization, string Body)> Requests { get; } = [];

    public ScriptedHandler Enqueue(HttpStatusCode status, string json, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return _responses.Dequeue()();
    }
}

/// <summary>JSON shaped like real api.github.com responses (trimmed to the fields the adapter reads).</summary>
internal static class Fixtures
{
    public const string IssueCreated = """
        {"id":2001,"number":42,"state":"open","title":"Crash on startup","html_url":"https://github.com/acme/widgets/issues/42","user":{"login":"bazaar-agent[bot]"}}
        """;

    public const string PullCreated = """
        {"id":3001,"number":43,"state":"open","draft":true,"html_url":"https://github.com/acme/widgets/pull/43"}
        """;

    public const string CommentCreated = """
        {"id":4001,"html_url":"https://github.com/acme/widgets/issues/7#issuecomment-4001","body":"hi"}
        """;

    public const string SearchEmpty = """{"total_count":0,"incomplete_results":false,"items":[]}""";

    public const string SearchFound = """
        {"total_count":1,"incomplete_results":false,"items":[
          {"id":2001,"number":42,"html_url":"https://github.com/acme/widgets/issues/42","body":"Crash\n\n<!-- bazaar-draft:11111111-2222-3333-4444-555555555555 -->"}]}
        """;

    public const string SearchNearMiss = """
        {"total_count":1,"incomplete_results":false,"items":[
          {"id":9,"number":9,"html_url":"https://github.com/acme/widgets/issues/9","body":"mentions bazaar-draft but a different id"}]}
        """;

    public const string CommentsFound = """
        [{"id":1,"html_url":"https://github.com/acme/widgets/issues/7#issuecomment-1","body":"unrelated"},
         {"id":4001,"html_url":"https://github.com/acme/widgets/issues/7#issuecomment-4001","body":"x\n\n<!-- bazaar-draft:11111111-2222-3333-4444-555555555555 -->"}]
        """;

    public const string NotFound = """{"message":"Not Found","documentation_url":"https://docs.github.com/rest"}""";

    public const string SecondaryRateLimit = """
        {"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.","documentation_url":"https://docs.github.com/rest"}
        """;
}
