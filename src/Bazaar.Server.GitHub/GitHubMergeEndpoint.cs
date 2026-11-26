using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Git;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Bazaar.Server.GitHub.Webhooks;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitHub;

/// <summary>
/// <c>POST /github/merge</c> (doc 21, Merge route): the client sends the owner's signed approval of one squash merge. The route
/// authenticates the caller, requires the caller to be the human the approval names, then hands the merge to
/// <see cref="IOwnerSignedMergeService"/>, which checks the signature, the head sha and the checks before any call that changes GitHub.
/// </summary>
public static class GitHubMergeEndpoint
{
    private const int _maxBodyBytes = 16 * 1024;

    /// <summary>
    /// Maps <c>POST /github/merge</c>. The host calls this next to <c>MapBazaarHttpSurface</c> and passes its guard
    /// (<c>HttpSurfaceEndpoints.Guarded</c>), so this module does not reference the server project.
    /// </summary>
    public static IEndpointRouteBuilder MapBazaarGitHubRoutes(
        this IEndpointRouteBuilder routes,
        Func<HttpContext, Func<HttpContext, PrincipalRef, Task<IResult>>, Task<IResult>> guarded)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guarded);
        routes.MapPost("/github/merge", (Func<HttpContext, Task<IResult>>)(http => guarded(http, MergeAsync)));
        routes.MapBazaarGitHubWebhook();
        return routes;
    }

    private static async Task<IResult> MergeAsync(HttpContext http, PrincipalRef caller)
    {
        // Read at most one byte past the cap, so a chunked body without a Content-Length is bounded too.
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _maxBodyBytes)
                return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        }

        if (buffer.Length == 0)
            return Error(StatusCodes.Status400BadRequest, "bad_request");

        GitHubMergeRequestDto? body;
        try
        {
            body = JsonSerializer.Deserialize(buffer.ToArray(), GitHubContractsJsonContext.Default.GitHubMergeRequestDto);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        if (body?.Pr is null || body.Merge is null || body.Approval is null
            || string.IsNullOrWhiteSpace(body.Pr.Owner) || string.IsNullOrWhiteSpace(body.Pr.Repo) || body.Pr.Number < 1
            || string.IsNullOrEmpty(body.Merge.HeadSha) || body.Merge.CommitTitle is null || body.Merge.CommitMessage is null
            || body.Approval.ArgsHash is null || body.Approval.Nonce is null || body.Approval.Signature is null
            || body.Approval.DeviceId is null || body.Approval.Action is null)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        // The approval names a human. Only that human, signed in, may present it (defence in depth beside the signature check).
        if (caller.Kind != "human" || caller.Id != body.Approval.HumanId)
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var services = http.RequestServices;
        var installation = services.GetRequiredService<GitHubMergeOptions>().InstallationId;
        if (installation <= 0)
            return Error(StatusCodes.Status502BadGateway, "github_unavailable");

        OwnerMergeApproval approval;
        MergeRequest merge;
        try
        {
            var pr = new PrRef(installation, body.Pr.Owner, body.Pr.Repo, body.Pr.Number);
            merge = new MergeRequest(body.Merge.HeadSha, body.Merge.CommitTitle, body.Merge.CommitMessage);
            var a = body.Approval;
            approval = new OwnerMergeApproval(
                pr,
                new OwnerPayload(
                    a.Action,
                    a.ApprovalId,
                    a.ArgsHash,
                    a.HeadSha,
                    a.HumanId,
                    a.DeviceId,
                    DateTimeOffset.FromUnixTimeMilliseconds(a.IssuedAtMs),
                    DateTimeOffset.FromUnixTimeMilliseconds(a.ExpiresAtMs),
                    a.Nonce),
                a.Signature);
        }
        catch (ArgumentException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        try
        {
            await services.GetRequiredService<IOwnerSignedMergeService>().MergeAsync(approval, merge, http.RequestAborted);
            return Results.Json(new GitHubMergeResultDto(true), GitHubContractsJsonContext.Default.GitHubMergeResultDto);
        }
        catch (MergeNotApprovedException ex)
        {
            return Error(StatusCodes.Status403Forbidden, "merge_not_approved", ex.Message);
        }
        catch (ArgumentException)
        {
            // A field the signed layout refuses (a line break, a bad nonce length): the request is malformed, not forged.
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubTokenException or GitHubStaleHeadException or HttpRequestException or TaskCanceledException)
        {
            return Error(StatusCodes.Status502BadGateway, "github_unavailable");
        }
    }

    private static IResult Error(int status, string code, string? reason = null) =>
        Results.Json(new GitHubMergeErrorDto(code, reason), GitHubContractsJsonContext.Default.GitHubMergeErrorDto, statusCode: status);
}
