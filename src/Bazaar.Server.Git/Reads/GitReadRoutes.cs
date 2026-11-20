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
/// Nested owners (doc 52, PROPOSED): <c>GET /git/gitlab/repos/{**path}</c> takes a GitLab subgroup project such as
/// <c>group/sub/sub2/project</c>. ASP.NET cannot put a catch-all in front of a fixed suffix, so one catch-all route parses the
/// fixed tail (<c>prs</c>, <c>prs/{n}</c>, <c>prs/{n}/files</c>, <c>prs/{n}/checks</c>) off the end of the path and the repo is
/// everything before it. Only providers in <see cref="_nestedOwnerProviders"/> may name a repo with more than one owner segment;
/// for every other provider such a path has no route (404), as before. Clients send literal slashes: an encoded <c>%2F</c> stays
/// inside one segment (Kestrel keeps it), so it matches no route.
/// </remarks>
public static partial class GitReadRoutes
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"^[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    /// <summary>Providers whose repos may have nested owners (GitLab subgroups). Every other provider keeps one owner segment.</summary>
    private static readonly string[] _nestedOwnerProviders = ["gitlab"];

    /// <summary>The most segments a repo path may have: GitLab allows twenty groups above a project.</summary>
    private const int _maxRepoSegments = 21;

    public static IEndpointRouteBuilder MapBazaarGitReads(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs",
            (string provider, string owner, string repo, string? state, int? page, int? pageSize, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                ListAsync(provider, $"{owner}/{repo}", state, page, pageSize, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}",
            (string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                PrAsync(provider, $"{owner}/{repo}", n, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}/files",
            (string provider, string owner, string repo, string n, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                FilesAsync(provider, $"{owner}/{repo}", n, head, reads, logs, ct)));

        guard(app.MapGet("/git/{provider}/repos/{owner}/{repo}/prs/{n}/checks",
            (string provider, string owner, string repo, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                ChecksAsync(provider, $"{owner}/{repo}", n, reads, logs, ct)));

        // The catch-all sits last: ASP.NET ranks it below the fixed routes above, so one-segment owners never reach it.
        guard(app.MapGet("/git/{provider}/repos/{**path}",
            (string provider, string path, string? state, int? page, int? pageSize, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct) =>
                NestedAsync(provider, path, state, page, pageSize, head, reads, logs, ct)));

        return app;
    }

    /// <summary>The fixed tail of a nested repo path, after the repo itself.</summary>
    private enum NestedTail
    {
        List,
        Pr,
        Files,
        Checks,
    }

    internal static Task<IResult> NestedAsync(string provider, string path, string? state, int? page, int? pageSize, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!SplitNested(path, out var fullName, out var tail, out var n))
            return Task.FromResult(Results.NotFound());

        // A repo with more owner segments than GitHub allows has no route for that provider, so it stays a plain 404.
        if (fullName.Split('/').Length > 2 && !_nestedOwnerProviders.Contains(provider, StringComparer.Ordinal))
            return Task.FromResult(Results.NotFound());

        return tail switch
        {
            NestedTail.List => ListAsync(provider, fullName, state, page, pageSize, reads, logs, ct),
            NestedTail.Pr => PrAsync(provider, fullName, n, reads, logs, ct),
            NestedTail.Files => FilesAsync(provider, fullName, n, head, reads, logs, ct),
            _ => ChecksAsync(provider, fullName, n, reads, logs, ct),
        };
    }

    /// <summary>
    /// Splits <c>{repo}/prs[/{n}[/files|/checks]]</c> into the repo and its tail. The fixed suffix is matched from the end, so the
    /// repo may hold any number of slashes, and a project named <c>prs</c> still resolves. A PR suffix needs a numeric <paramref name="n"/>;
    /// any other shape is not a route.
    /// </summary>
    private static bool SplitNested(string path, out string fullName, out NestedTail tail, out string n)
    {
        var segments = path.Split('/');
        var count = segments.Length;
        fullName = string.Empty;
        tail = NestedTail.List;
        n = string.Empty;

        int suffix;
        if (count >= 5 && segments[^3] == "prs" && IsNumber(segments[^2]) && segments[^1] == "files")
        {
            (suffix, tail, n) = (3, NestedTail.Files, segments[^2]);
        }
        else if (count >= 5 && segments[^3] == "prs" && IsNumber(segments[^2]) && segments[^1] == "checks")
        {
            (suffix, tail, n) = (3, NestedTail.Checks, segments[^2]);
        }
        else if (count >= 4 && segments[^2] == "prs" && IsNumber(segments[^1]))
        {
            (suffix, tail, n) = (2, NestedTail.Pr, segments[^1]);
        }
        else if (count >= 3 && segments[^1] == "prs")
        {
            (suffix, tail) = (1, NestedTail.List);
        }
        else
        {
            return false;
        }

        // The suffix length already keeps at least two segments for the repo.
        fullName = string.Join('/', segments, 0, count - suffix);
        return true;
    }

    private static bool IsNumber(string text) => NumberRegex().IsMatch(text);

    internal static Task<IResult> ListAsync(string provider, string fullName, string? state, int? page, int? pageSize, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidRepo(provider, fullName))
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
            var dto = await reads.ListPrsAsync(new RepoRef(provider, fullName), parsed, page ?? 1, pageSize, ct).ConfigureAwait(false);
            return Results.Json(dto, GitReadContractsJsonContext.Default.GitPrPageDto);
        });
    }

    internal static Task<IResult> PrAsync(string provider, string fullName, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, fullName, n, out var pr))
            return Task.FromResult(BadRequest("bad_request"));

        return Run(logs, async () =>
        {
            var dto = await reads.GetPrAsync(pr, ct).ConfigureAwait(false);
            return Results.Json(dto, GitReadContractsJsonContext.Default.GitPrDetailDto);
        });
    }

    internal static Task<IResult> FilesAsync(string provider, string fullName, string n, string? head, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, fullName, n, out var pr))
            return Task.FromResult(BadRequest("bad_request"));
        if (head is null || !ShaRegex().IsMatch(head))
            return Task.FromResult(BadRequest("bad_head"));

        return Run(logs, async () =>
        {
            var files = await reads.GetFilesAsync(pr, head, ct).ConfigureAwait(false);
            return Results.Json(files.ToArray(), GitReadContractsJsonContext.Default.GitPrFileDtoArray);
        });
    }

    internal static Task<IResult> ChecksAsync(string provider, string fullName, string n, IGitReadService reads, ILoggerFactory logs, CancellationToken ct)
    {
        if (!ValidPr(provider, fullName, n, out var pr))
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

    /// <summary>A provider id and a repo of two to <see cref="_maxRepoSegments"/> segments, each one a valid name.</summary>
    private static bool ValidRepo(string provider, string fullName)
    {
        if (!ProviderRegex().IsMatch(provider))
            return false;

        var segments = fullName.Split('/');
        return segments.Length is >= 2 and <= _maxRepoSegments && segments.All(s => SegmentRegex().IsMatch(s));
    }

    private static bool ValidPr(string provider, string fullName, string n, out PrRef pr)
    {
        pr = null!;
        if (!ValidRepo(provider, fullName)
            || !long.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        pr = new PrRef(provider, fullName, number);
        return true;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ErrorContext : System.Text.Json.Serialization.JsonSerializerContext;
