using System.Buffers;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Git;
using Bazaar.Server.Git.Reads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git.Merge;

/// <summary>
/// The neutral merge route (doc 52, Wire and routes): <c>POST /git/{provider}/repos/{owner}/{repo}/prs/{n}/merge</c>. The body is
/// <see cref="GitMergeRequestDto"/>; the call goes through <see cref="IOwnerSignedMergeGate"/> and nothing else. Answers:
/// 200 <c>merged</c>; 400 bad input; 403 <c>merge_not_approved</c> (no detail, so a probe learns nothing about which check failed);
/// 404 <c>unknown_provider</c> or <c>not_found</c>; 409 <c>head_moved</c> with the current sha; 429 <c>rate_limited</c> with
/// <c>Retry-After</c>; 502 <c>forge_unavailable</c>. The signature and every forge message stay out of logs and answers.
/// The repo is one path segment, as for the read routes (same limit for nested GitLab groups).
/// </summary>
public static class GitMergeRoutes
{
    private const int _maxBodyBytes = 16 * 1024;
    private const int _maxTextLength = 1000;

    public static IEndpointRouteBuilder MapBazaarGitMerge(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapPost("/git/{provider}/repos/{owner}/{repo}/prs/{n}/merge",
            (string provider, string owner, string repo, string n, HttpRequest request, IOwnerSignedMergeGate gate, ILoggerFactory logs, CancellationToken ct) =>
                MergeAsync(provider, owner, repo, n, request, gate, logs, ct)));

        return app;
    }

    internal static async Task<IResult> MergeAsync(string provider, string owner, string repo, string n, HttpRequest request, IOwnerSignedMergeGate gate, ILoggerFactory logs, CancellationToken ct)
    {
        if (!GitReadRoutes.ValidPr(provider, owner, repo, n, out var pr))
            return Error("bad_request", StatusCodes.Status400BadRequest);

        var dto = await ReadBodyAsync(request, ct).ConfigureAwait(false);
        if (dto is null || !TryBuild(pr, dto, out var approval, out var merge))
            return Error("bad_request", StatusCodes.Status400BadRequest);

        try
        {
            await gate.MergeAsync(approval, merge, ct).ConfigureAwait(false);
            return Results.Json(new GitMergedDto(merge.HeadSha), GitReadContractsJsonContext.Default.GitMergedDto);
        }
        catch (MergeNotApprovedException refused) when (refused.Code == "head_moved" && refused.CurrentHeadSha is not null)
        {
            return Results.Json(new GitHeadMovedDto(refused.CurrentHeadSha), GitReadContractsJsonContext.Default.GitHeadMovedDto, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MergeNotApprovedException)
        {
            return Error("merge_not_approved", StatusCodes.Status403Forbidden);
        }
        catch (UnknownForgeProviderException)
        {
            return Error("unknown_provider", StatusCodes.Status404NotFound);
        }
        catch (ForgeNotFoundException)
        {
            return Error("not_found", StatusCodes.Status404NotFound);
        }
        catch (ForgeRateLimitedException limited)
        {
            int? seconds = limited.RetryAfter is { } span ? (int)Math.Ceiling(Math.Max(0, span.TotalSeconds)) : null;
            if (seconds is { } s)
                request.HttpContext.Response.Headers.RetryAfter = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new GitRateLimitedDto("rate_limited", seconds), GitReadContractsJsonContext.Default.GitRateLimitedDto, statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The message can carry a forge URL or response text, so only the type is logged and nothing reaches the client.
            GitLog.ForgeMergeFailed(logs.CreateLogger("Bazaar.Server.Git.Merge"), ex.GetType().Name);
            return Error("forge_unavailable", StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<GitMergeRequestDto?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > _maxBodyBytes)
            return null;

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            var stream = request.Body;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.GetMemory(1024), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.Advance(read);
                if (buffer.WrittenCount > _maxBodyBytes)
                    return null;
            }

            return JsonSerializer.Deserialize(buffer.WrittenSpan, GitReadContractsJsonContext.Default.GitMergeRequestDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryBuild(PrRef pr, GitMergeRequestDto dto, out OwnerMergeApproval approval, out MergeRequest merge)
    {
        approval = null!;
        merge = null!;
        var a = dto.Approval;
        if (a is null
            || dto.HeadSha is null || !GitReadRoutes.ShaRegex().IsMatch(dto.HeadSha)
            || !Text(dto.CommitTitle) || !Text(dto.SignOff)
            || a.Action is null || a.DeviceId is null || a.ArgsHashBase64 is null || a.NonceBase64 is null || a.SignatureBase64 is null
            || a.HeadSha is not null && !GitReadRoutes.ShaRegex().IsMatch(a.HeadSha))
        {
            return false;
        }

        try
        {
            var payload = new OwnerPayload(
                a.Action, a.ApprovalId, Convert.FromBase64String(a.ArgsHashBase64), a.HeadSha, a.HumanId, a.DeviceId,
                a.IssuedAt, a.ExpiresAt, Convert.FromBase64String(a.NonceBase64));
            approval = new OwnerMergeApproval(pr, payload, Convert.FromBase64String(a.SignatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }

        merge = new MergeRequest(dto.HeadSha, dto.CommitTitle, dto.SignOff);
        return true;
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= _maxTextLength;

    private static IResult Error(string error, int status) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error }, ErrorContext.Default.DictionaryStringString, statusCode: status);
}
