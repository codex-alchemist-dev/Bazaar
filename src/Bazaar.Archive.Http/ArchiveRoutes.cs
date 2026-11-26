using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Archive.Http;

/// <summary>The host's authentication wrapper: runs the handler with the caller's id and kind, or answers the refusal itself.</summary>
public delegate Task<IResult> ArchiveGuard(HttpContext http, Func<HttpContext, Guid, string, Task<IResult>> run);

/// <summary>
/// Owner-only HTTP routes for cold storage (doc 25 "Archive cold storage"): <c>GET /archive/units</c>,
/// <c>POST /archive/units/{kind}/{id}</c> (archive), <c>POST .../verify</c>, <c>POST .../restore</c> and
/// <c>GET /archive/search?q=</c>. <c>kind</c> is <c>gc</c> or <c>project</c>. A caller who is not the owner gets 403;
/// a missing store, source or restore target answers 503.
/// </summary>
public static class ArchiveRoutes
{
    public static IEndpointRouteBuilder MapArchive(this IEndpointRouteBuilder routes, ArchiveGuard guarded)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guarded);

        Map(routes, "/archive/units", "GET", guarded, ListAsync);
        Map(routes, "/archive/units/{kind}/{id}", "POST", guarded, ArchiveAsync);
        Map(routes, "/archive/units/{kind}/{id}/verify", "POST", guarded, VerifyAsync);
        Map(routes, "/archive/units/{kind}/{id}/restore", "POST", guarded, RestoreAsync);
        Map(routes, "/archive/search", "GET", guarded, SearchAsync);
        return routes;
    }

    private static void Map(IEndpointRouteBuilder routes, string path, string method, ArchiveGuard guarded, Func<HttpContext, Guid, Task<IResult>> handler)
    {
        RequestDelegate del = async http =>
        {
            var result = await guarded(http, (h, id, kind) =>
                kind == "owner" ? handler(h, id) : Task.FromResult(Error(StatusCodes.Status403Forbidden, "forbidden")));
            await result.ExecuteAsync(http);
        };
        routes.Map(path, del).WithMetadata(new HttpMethodMetadata([method]));
    }

    private static IResult Error(int status, string code) => Results.Text($$"""{"error":"{{code}}"}""", "application/json", statusCode: status);

    private static bool TryUnit(HttpContext http, out ArchiveUnit unit)
    {
        var kind = http.Request.RouteValues["kind"]?.ToString();
        var id = http.Request.RouteValues["id"]?.ToString() ?? string.Empty;
        unit = new ArchiveUnit(ArchiveUnitKind.GroupChat, id);
        if (kind is not ("gc" or "project") || !ArchiveHashing.IsSafeId(id))
        {
            return false;
        }

        unit = new ArchiveUnit(kind == "gc" ? ArchiveUnitKind.GroupChat : ArchiveUnitKind.Project, id);
        return true;
    }

    private static Task<IResult> ListAsync(HttpContext http, Guid owner)
    {
        var store = http.RequestServices.GetService<IArchiveStore>();
        return store is null ? Task.FromResult(Error(StatusCodes.Status503ServiceUnavailable, "not_configured")) : ListCoreAsync(http, store);
    }

    private static async Task<IResult> ListCoreAsync(HttpContext http, IArchiveStore store)
    {
        var units = await store.ListUnitsAsync(http.RequestAborted);
        return Results.Json(new UnitList([.. units.Select(u => new UnitRow(KindName(u.Kind), u.Id))]), ArchiveHttpJson.Default.UnitList);
    }

    private static async Task<IResult> ArchiveAsync(HttpContext http, Guid owner)
    {
        if (!TryUnit(http, out var unit))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_unit");
        }

        var sp = http.RequestServices;
        if (sp.GetService<IArchiveStore>() is not { } store || sp.GetService<IArchiveSourceFactory>() is not { } factory)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        var archiver = new ColdArchiver(factory.CreateFor(owner), store, sp.GetService<IArchiveSearchIndex>(), sp.GetRequiredService<ArchiveOptions>(), sp.GetRequiredService<TimeProvider>());
        try
        {
            var manifest = await archiver.ArchiveAsync(unit, http.RequestAborted);
            return Results.Json(new ArchiveResult(KindName(unit.Kind), unit.Id, manifest.RecordCount, manifest.Segments.Count, manifest.RootHash), ArchiveHttpJson.Default.ArchiveResult);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already archived", StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status409Conflict, "already_archived");
        }
        catch (InvalidOperationException)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "refused");
        }
    }

    private static async Task<IResult> VerifyAsync(HttpContext http, Guid owner)
    {
        if (!TryUnit(http, out var unit))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_unit");
        }

        if (http.RequestServices.GetService<IArchiveStore>() is not { } store)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        var result = await new ArchiveVerifier(store).VerifyAsync(unit, http.RequestAborted);
        return Results.Json(new VerifyResult(result.Ok, [.. result.Problems], result.Manifest?.RootHash), ArchiveHttpJson.Default.VerifyResult);
    }

    private static async Task<IResult> RestoreAsync(HttpContext http, Guid owner)
    {
        if (!TryUnit(http, out var unit))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_unit");
        }

        var sp = http.RequestServices;
        if (sp.GetService<IArchiveStore>() is not { } store || sp.GetService<IArchiveRestoreTarget>() is not { } target)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        try
        {
            var r = await new ArchiveRestorer(store, target).RestoreAsync(unit, http.RequestAborted);
            return Results.Json(new RestoreResult(r.Restored, r.AlreadyPresent, [.. r.Conflicts]), ArchiveHttpJson.Default.RestoreResult);
        }
        catch (NotSupportedException)
        {
            return Error(StatusCodes.Status409Conflict, "cannot_reinsert");
        }
        catch (InvalidOperationException)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "verify_failed");
        }
    }

    private static async Task<IResult> SearchAsync(HttpContext http, Guid owner)
    {
        var q = http.Request.Query["q"].ToString();
        if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_query");
        }

        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 100) : 20;
        var index = http.RequestServices.GetService<IArchiveSearchIndex>();
        if (index is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        if (http.RequestServices.GetService<ArchiveIndexRebuilder>() is { } rebuilder)
        {
            await rebuilder.EnsureBuiltAsync(http.RequestAborted);
        }

        var hits = await index.SearchAsync(q, limit, http.RequestAborted);
        return Results.Json(new SearchResult([.. hits.Select(h => new HitRow(KindName(h.Unit.Kind), h.Unit.Id, h.RecordId, h.At, h.Snippet))]), ArchiveHttpJson.Default.SearchResult);
    }

    private static string KindName(ArchiveUnitKind kind) => kind == ArchiveUnitKind.GroupChat ? "gc" : "project";
}

internal sealed record UnitRow(string Kind, string Id);

internal sealed record UnitList(UnitRow[] Units);

internal sealed record ArchiveResult(string Kind, string Id, int Records, int Segments, string RootHash);

internal sealed record VerifyResult(bool Ok, string[] Problems, string? RootHash);

internal sealed record RestoreResult(int Restored, int AlreadyPresent, string[] Conflicts);

internal sealed record HitRow(string Kind, string Unit, string RecordId, DateTimeOffset At, string Snippet);

internal sealed record SearchResult(HitRow[] Hits);

[JsonSerializable(typeof(UnitList))]
[JsonSerializable(typeof(ArchiveResult))]
[JsonSerializable(typeof(VerifyResult))]
[JsonSerializable(typeof(RestoreResult))]
[JsonSerializable(typeof(SearchResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ArchiveHttpJson : JsonSerializerContext;
