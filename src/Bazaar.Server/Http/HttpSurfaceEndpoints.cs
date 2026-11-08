using System.Globalization;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Http.Blobs;
using Bazaar.Server.Providers;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Http;

/// <summary>
/// Routes of the authenticated HTTP surface (doc 02 WP10). Every route authenticates first (401), then spends a token of the
/// HTTP bucket (429), then runs. Errors never carry paths or internal detail.
/// <list type="bullet">
/// <item><c>POST /blobs</c> opens a resumable upload (<c>Upload-Length</c>, optional <c>Upload-Sha256</c>).</item>
/// <item><c>HEAD|PATCH /blobs/uploads/{id}</c> reports the offset or appends a chunk at <c>Upload-Offset</c>.</item>
/// <item><c>GET /blobs/{hash}</c> serves a blob with range support; a non-viewer gets 404.</item>
/// <item><c>GET /search?gc=&amp;q=</c> searches one GC, answering only what the caller can read.</item>
/// <item><c>POST /constellation/tiles</c> returns the binary Constellation tile for one viewport (doc 19).</item>
/// </list>
/// Pairing routes (<c>/pair/*</c>) are not part of this package: doc 32 leaves their detail to a spike.
/// </summary>
public static class HttpSurfaceEndpoints
{
    private const int _maxQueryLength = 200;

    /// <summary>Maps the routes. The host must expose them on a listener that allows POST and PATCH.</summary>
    public static IEndpointRouteBuilder MapBazaarHttpSurface(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/blobs", (Func<HttpContext, Task<IResult>>)(http => Guarded(http, CreateUploadAsync)));
        routes.MapMethods("/blobs/uploads/{id}", ["HEAD"], (HttpContext http, string id) => Guarded(http, (h, p) => UploadStatusAsync(h, p, id)));
        routes.MapMethods("/blobs/uploads/{id}", ["PATCH"], (HttpContext http, string id) => Guarded(http, (h, p) => AppendAsync(h, p, id)));
        routes.MapGet("/blobs/{hash}", (HttpContext http, string hash) => Guarded(http, (h, p) => DownloadAsync(h, p, hash)));
        routes.MapGet("/search", (Func<HttpContext, Task<IResult>>)(http => Guarded(http, SearchAsync)));
        routes.MapPost("/constellation/tiles", (Func<HttpContext, Task<IResult>>)(http => Guarded(http, ConstellationTileAsync)));
        // TODO(component): pairing routes (POST /pair/offers, POST /pair/token) wait for the doc 32 spike.
        return routes;
    }

    private static async Task<IResult> Guarded(HttpContext http, Func<HttpContext, PrincipalRef, Task<IResult>> run)
    {
        var services = http.RequestServices;
        var wire = services.GetRequiredService<IOptions<WireOptions>>().Value;

        // Credentials never travel in clear text off the machine unless the operator allowed an overlay network.
        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token)) return Unauthorized(http);

        var auth = await services.GetRequiredService<IConnectionAuthenticator>()
            .AuthenticateAsync(new HelloCredential(token), new DeviceInfo("http", "http", string.Empty), http.RequestAborted);
        if (!auth.Ok || auth.Principal is null || (auth.ExpiresAt is { } expiry && expiry <= services.GetRequiredService<TimeProvider>().GetUtcNow()))
            return Unauthorized(http);

        if (!services.GetRequiredService<HttpRateLimiter>().TryAcquire(auth.Principal.Id, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        return await run(http, auth.Principal);
    }

    private const int _maxTileBodyBytes = 4096;

    private static async Task<IResult> ConstellationTileAsync(HttpContext http, PrincipalRef principal)
    {
        var length = http.Request.ContentLength;
        if (length is null or < 1) return Error(StatusCodes.Status400BadRequest, "bad_request");
        if (length > _maxTileBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");

        ConstellationTileRequestBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(http.Request.Body, ConstellationTileJsonContext.Default.ConstellationTileRequestBody, http.RequestAborted);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.ViewId)
            || !Enum.TryParse<ZoomTier>(body.Tier, ignoreCase: false, out var tier) || !Enum.IsDefined(tier))
            return Error(StatusCodes.Status400BadRequest, "bad_request");

        ViewportRequest request;
        try
        {
            request = new ViewportRequest(body.ViewId, body.GraphVersion, tier, body.MinX, body.MinY, body.MinZ, body.MaxX, body.MaxY, body.MaxZ, body.MaxNodes);
        }
        catch (ArgumentException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var response = await http.RequestServices.GetRequiredService<ConstellationTileHandler>().HandleAsync(request, http.RequestAborted);
        http.Response.StatusCode = response.StatusCode;
        http.Response.ContentType = response.ContentType;
        await http.Response.Body.WriteAsync(response.Body, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> CreateUploadAsync(HttpContext http, PrincipalRef principal)
    {
        var options = http.RequestServices.GetRequiredService<HttpSurfaceOptions>();
        if (!long.TryParse(http.Request.Headers["Upload-Length"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1)
            return Error(StatusCodes.Status400BadRequest, "bad_upload_length");
        if (length > options.MaxBlobBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");

        string? declared = null;
        var declaredHeader = http.Request.Headers["Upload-Sha256"].ToString();
        if (declaredHeader.Length > 0)
        {
            if (!BlobIds.TryNormalizeHash(declaredHeader, out var normalized)) return Error(StatusCodes.Status400BadRequest, "bad_hash");
            declared = normalized;
        }

        var session = await http.RequestServices.GetRequiredService<IBlobStore>()
            .CreateUploadAsync(principal.Id, length, declared, http.RequestAborted);
        if (session is null) return Error(StatusCodes.Status429TooManyRequests, "too_many_uploads");
        return Results.Json(new { upload_id = session.Id, offset = 0L, length }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UploadStatusAsync(HttpContext http, PrincipalRef principal, string id)
    {
        var session = await http.RequestServices.GetRequiredService<IBlobStore>().GetUploadAsync(principal.Id, id, http.RequestAborted);
        if (session is null) return Error(StatusCodes.Status404NotFound, "not_found");
        http.Response.Headers["Upload-Offset"] = session.Offset.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["Upload-Length"] = session.Length.ToString(CultureInfo.InvariantCulture);
        return Results.NoContent();
    }

    private static async Task<IResult> AppendAsync(HttpContext http, PrincipalRef principal, string id)
    {
        var options = http.RequestServices.GetRequiredService<HttpSurfaceOptions>();
        if (!long.TryParse(http.Request.Headers["Upload-Offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            return Error(StatusCodes.Status400BadRequest, "bad_offset");

        // The size is checked from the header before a byte of the body is read.
        if (http.Request.ContentLength is not { } count) return Error(StatusCodes.Status411LengthRequired, "length_required");
        if (count < 1) return Error(StatusCodes.Status400BadRequest, "empty_chunk");
        if (count > options.MaxChunkBytes) return Error(StatusCodes.Status413PayloadTooLarge, "chunk_too_large");
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = count;

        var result = await http.RequestServices.GetRequiredService<IBlobStore>()
            .AppendAsync(principal.Id, id, offset, http.Request.Body, count, http.RequestAborted);
        http.Response.Headers["Upload-Offset"] = result.Offset.ToString(CultureInfo.InvariantCulture);
        return result.Status switch
        {
            AppendStatus.Accepted => Results.NoContent(),
            AppendStatus.Completed => Results.Json(new { hash = result.Hash, size = result.Offset }),
            AppendStatus.OffsetMismatch => Error(StatusCodes.Status409Conflict, "offset_mismatch"),
            AppendStatus.HashMismatch => Error(StatusCodes.Status422UnprocessableEntity, "hash_mismatch"),
            AppendStatus.BadChunk => Error(StatusCodes.Status400BadRequest, "bad_chunk"),
            _ => Error(StatusCodes.Status404NotFound, "not_found"),
        };
    }

    private static async Task<IResult> DownloadAsync(HttpContext http, PrincipalRef principal, string hash)
    {
        if (!BlobIds.TryNormalizeHash(hash, out var id)) return Error(StatusCodes.Status404NotFound, "not_found");
        var services = http.RequestServices;
        if (!await services.GetRequiredService<BlobReadAccess>().CanReadAsync(id, ToViewer(principal), http.RequestAborted))
            return Error(StatusCodes.Status404NotFound, "not_found");
        var blob = await services.GetRequiredService<IBlobStore>().OpenAsync(id, http.RequestAborted);
        if (blob is null) return Error(StatusCodes.Status404NotFound, "not_found");

        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(blob.Content, "application/octet-stream", fileDownloadName: id, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}\""), enableRangeProcessing: true);
    }

    private static async Task<IResult> SearchAsync(HttpContext http, PrincipalRef principal)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<HttpSurfaceOptions>();
        var query = http.Request.Query["q"].ToString();
        if (!Guid.TryParse(http.Request.Query["gc"].ToString(), out var gcId) || query.Length is 0 or > _maxQueryLength)
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        var limit = int.TryParse(http.Request.Query["limit"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var asked)
            ? Math.Clamp(asked, 1, options.SearchMaxResults)
            : Math.Min(20, options.SearchMaxResults);

        var access = services.GetService<Gc.Read.IGcReadAccess>();
        var search = services.GetService<ISearchProvider>();
        if (access is null || search is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        // A GC the caller cannot read looks exactly like one that does not exist.
        var grant = await access.GetGrantAsync(gcId, ToViewer(principal), http.RequestAborted);
        if (!grant.Allowed) return Error(StatusCodes.Status404NotFound, "not_found");

        // Documents are indexed with scope "gc:<gc id>" and the entry seq as id. Over-fetch, then drop what is past the viewer's left_seq.
        var hits = await search.QueryAsync(new SearchQuery(GcSearchScope(gcId), query, options.SearchMaxResults * 4), http.RequestAborted);
        var results = new List<object>();
        foreach (var hit in hits)
        {
            if (!long.TryParse(hit.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) continue;
            if (grant.ThroughSeq is { } through && seq > through) continue;
            results.Add(new { gc = gcId, seq, score = hit.Score });
            if (results.Count == limit) break;
        }

        return Results.Json(new { results });
    }

    /// <summary>The search scope under which a GC's documents are indexed.</summary>
    public static string GcSearchScope(Guid gcId) => "gc:" + gcId.ToString("D");

    private static GcPrincipal ToViewer(PrincipalRef principal) => new(
        principal.Id,
        principal.Kind switch
        {
            "owner" => PrincipalKind.Owner,
            "agent" => PrincipalKind.Agent,
            _ => PrincipalKind.Human,
        },
        string.Empty);

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Error(StatusCodes.Status401Unauthorized, "unauthorized");
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);
}
