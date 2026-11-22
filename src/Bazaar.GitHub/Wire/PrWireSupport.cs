using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.GitHub.Wire;

/// <summary>Shared rules of the PR wire handlers: owner only, schema errors, and GitHub failures.</summary>
internal static class PrWireSupport
{
    /// <summary>Only the owner principal reaches a PR message. Anyone else gets <c>not_found</c>, as the board does. Agents never do.</summary>
    public static bool IsOwner(WireRequestContext context) => context.Connection.Principal.Kind == "owner";

    public static bool TryDecode<T>(WireRequestContext context, out T request, out WireReply? failure)
        where T : class
    {
        try
        {
            request = context.Payload<T>();
            failure = null;
            return true;
        }
        catch (WireFormatException)
        {
            request = null!;
            failure = WireReply.Fail(WireErrorCodes.BadRequest, "The request does not fit its schema.");
            return false;
        }
    }

    /// <summary>Maps a GitHub failure to a wire error: rate limits keep their retry time, anything else is internal.</summary>
    public static WireReply MapGitHubFailure(GitHubApiException error, TimeProvider time) => error switch
    {
        GitHubRateLimitedException limited => WireReply.Fail(
            WireErrorCodes.RateLimited,
            "GitHub is rate limiting this installation.",
            (int)Math.Clamp((limited.ResetAt - time.GetUtcNow()).TotalMilliseconds, 1000, int.MaxValue)),
        _ => WireReply.Fail(WireErrorCodes.Internal, "GitHub could not be reached."),
    };
}
