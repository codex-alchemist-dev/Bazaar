using System.Text.RegularExpressions;
using Bazaar.Contracts.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git.Reads;

/// <summary>
/// The guarded read routes (doc 52, Wire and routes): <c>GET /git/{provider}/repos/{owner}/{repo}/prs</c> and its sub-routes.
/// Host composition passes the HTTP surface guard (bearer, rate bucket) as <c>guard</c>, so this module never references
/// Bazaar.Server. Answers: 200 JSON from the source-generated context, 400 bad input, 404 <c>unknown_provider</c>,
/// 409 <c>head_moved</c> with the current sha, 502 <c>forge_unavailable</c>.
/// </summary>
/// <remarks>
/// Limit: the repo is one path segment, so a GitLab project in a nested subgroup (<c>group/sub/project</c>) cannot be addressed.
/// A catch-all would have to sit before the fixed <c>/prs</c> suffix, which ASP.NET routing does not allow. A later route shape
/// (for example a query parameter or an encoded project id) must lift this limit.
/// </remarks>
public static partial class GitReadRoutes
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShaRegex();

    public static IEndpointRouteBuilder MapBazaarGitReads(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs",
            (string provider, string owner, string repo, string? state, int? page, int? pageSize, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                ListAsync(provider, owner, repo, state, page, pageSize, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}",
            (string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                PrAsync(provider, owner, repo, n, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}/files",
            (string provider, string owner, string repo, string n, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                FilesAsync(provider, owner, repo, n, head, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}/checks",
            (string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                ChecksAsync(provider, owner, repo, n, reads, logs, ct)));

        return app;
    }

    internal static Task<IResult> ListAsync(string provider, string owner, string repo, string? state, int? page, int? pageSize, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidRepo(provider, owner, repo))
            return Task.FromResult(BadRequest("bad_repo"));

        PrState? parsed = null;
        if (state is not null)
        {
            if (!Enum.TryParse<PrState>(state, ignoreCase: true, out var s) || !Enum.IsDefined(s))
                return Task.FromResult(BadRequest("bad_state"));
            parsed = s;
        }

        if (page is < 1 || pageSize is < 1)
            return Task.FromResult(BadRequest("bad_page"));

        return Run(logs, async () =>
        {
            var dto = await reads.ListPrsAsync(new RepoRef(provider, $"{owner}/{repo}"), parsed, page ?? 1, pageSize, ct).ConfigureAwait(false);
            return Results.Json(dto, GitReadContractsJsonContext.Default.GitPrPageDto);
        });
    }

    internal static Task<IResult> PrAsync(string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, owner, repo, n, out var pr))
            return Task.FromResult(BadRequest("bad_request"));

        return Run(logs, async () =>
        {
            var dto = await reads.GetPrAsync(pr, ct).ConfigureAwait(false);
            return Results.Json(dto, GitReadContractsJsonContext.Default.GitPrDetailDto);
        });
    }

    internal static Task<IResult> FilesAsync(string provider, string owner, string repo, string n, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, owner, repo, n, out var pr))
            return Task.FromResult(BadRequest("bad_request"));
        if (head is null || !ShaRegex().IsMatch(head))
            return Task.FromResult(BadRequest("bad_head"));

        return Run(logs, async () =>
        {
            var files = await reads.GetFilesAsync(pr, head, ct).ConfigureAwait(false);
            return Results.Json(files.ToArray(), GitReadContractsJsonContext.Default.GitPrFileDtoArray);
        });
    }

    internal static Task<IResult> ChecksAsync(string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, owner, repo, n, out var pr))
            return Task.FromResult(BadRequest("bad_request"));

        return Run(logs, async () =>
        {
            var checks = await reads.GetChecksAsync(pr, ct).ConfigureAwait(false);
            return Results.Json(checks.ToArray(), GitReadContractsJsonContext.Default.GitCheckDtoArray);
        });
    }

    private static async Task<IResult> Run(ILoggerFactory logs, Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (UnknownForgeProviderException)
        {
            return Results.Json(new Dictionary<string, string> { ["error"] = "unknown_provider" }, ErrorContext.Default.DictionaryStringString, statusCode: StatusCodes.Status404NotFound);
        }
        catch (HeadMovedException moved)
        {
            return Results.Json(new GitHeadMovedDto(moved.CurrentSha), GitReadContractsJsonContext.Default.GitHeadMovedDto, statusCode: StatusCodes.Status409Conflict);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The message can carry a forge URL or response text, so only the type is logged and nothing reaches the client.
            GitLog.ForgeReadFailed(logs.CreateLogger("Bazaar.Server.Git.Reads"), ex.GetType().Name);
            return Results.Json(new Dictionary<string, string> { ["error"] = "forge_unavailable" }, ErrorContext.Default.DictionaryStringString, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult BadRequest(string error) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error }, ErrorContext.Default.DictionaryStringString, statusCode: StatusCodes.Status400BadRequest);

    private static bool ValidRepo(string provider, string owner, string repo) =>
        ProviderRegex().IsMatch(provider) && SegmentRegex().IsMatch(owner) && SegmentRegex().IsMatch(repo);

    private static bool ValidPr(string provider, string owner, string repo, string n, out PrRef pr)
    {
        pr = null!;
        if (!ValidRepo(provider, owner, repo)
            || !long.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        pr = new PrRef(provider, $"{owner}/{repo}", number);
        return true;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ErrorContext : System.Text.Json.Serialization.JsonSerializerContext;
