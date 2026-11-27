using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;

namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>Maps Bazaar.Server.GitHub failures to <see cref="CommunityPostException"/>. Messages are fixed text plus a status code; they never echo a token.</summary>
internal static class BridgeFailure
{
    public static CommunityPostException Refusal(string message) => new(message, retryable: false);

    public static CommunityPostException From(Exception ex, string what) => ex switch
    {
        GitHubRateLimitedException => new($"{what}: GitHub rate-limited the call.", retryable: true, ex),
        GitHubTokenException t => Status(what, "token exchange", t.StatusCode, ex),
        GitHubApiException a => Status(what, "GitHub call", (int?)a.StatusCode, ex),
        HttpRequestException h => Status(what, "GitHub call", (int?)h.StatusCode, ex),
        TimeoutException or TaskCanceledException => new($"{what}: the GitHub request timed out.", retryable: true, ex),
        _ => new($"{what}: unexpected failure ({ex.GetType().Name}).", retryable: true, ex),
    };

    // No response, 5xx, 408 and 429 may succeed later. Any other 4xx is a refusal.
    private static CommunityPostException Status(string what, string step, int? status, Exception inner)
    {
        var retryable = status is null or >= 500 or 408 or 429;
        var code = status is null ? "no response" : $"HTTP {status}";
        return new($"{what}: {step} failed ({code}).", retryable, inner);
    }
}
