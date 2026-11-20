using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using Bazaar.Wire.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Bazaar.Server.Wire.Discovery;

/// <summary>Composition and routes of the wire discovery module (doc 90 section 6).</summary>
public static class WireDiscoveryEndpoints
{
    /// <summary>
    /// Registers the catalog, the limiter and the options. The host must also register an <see cref="IWireSchemaSource"/>
    /// (for example <c>EmbeddedWireSchemaSource</c> from <c>Bazaar.Wire.Schema</c>); transport and codec modules register an
    /// <see cref="IWireDiscoveryContribution"/> each.
    /// </summary>
    public static IServiceCollection AddBazaarWireDiscovery(this IServiceCollection services, Action<WireDiscoveryOptions>? configure = null)
    {
        var options = services.AddOptions<WireDiscoveryOptions>()
            .Validate(o => o.IsValid(out _), "WireDiscoveryOptions are invalid (limits must be positive, BasePath must start with '/').")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWireDiscoveryCatalog, WireDiscoveryCatalog>();
        services.TryAddSingleton<WireDiscoveryService>();
        services.TryAddSingleton<FixedWindowClientLimiter>();
        return services;
    }

    /// <summary>
    /// Maps <c>GET {BasePath}</c> and <c>GET {BasePath}/schema/{name}</c>, anonymous and read-only. Both routes answer 404 while
    /// <see cref="WireDiscoveryOptions.Enabled"/> is false. Behind a proxy the host adds forwarded-headers handling so the limiter sees client addresses.
    /// </summary>
    public static RouteGroupBuilder MapBazaarWireDiscovery(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<WireDiscoveryOptions>>().Value;
        var group = endpoints.MapGroup(settings.BasePath).AllowAnonymous();
        // RequestDelegate overloads keep the routes free of reflection over handler parameters (AOT and trim clean).
        group.MapGet("", static context => context.RequestServices.GetRequiredService<WireDiscoveryService>().Document(context).ExecuteAsync(context));
        group.MapGet(
            "schema/{**name}",
            static context => context.RequestServices.GetRequiredService<WireDiscoveryService>()
                .Schema(context, context.Request.RouteValues["name"] as string ?? string.Empty).ExecuteAsync(context));
        return group;
    }
}

/// <summary>Request handling behind the two routes.</summary>
internal sealed class WireDiscoveryService(
    IWireSchemaSource schemas,
    IWireDiscoveryCatalog catalog,
    FixedWindowClientLimiter limiter,
    IOptionsMonitor<WireDiscoveryOptions> options,
    IEnumerable<WireDiscoveryLiveSettings> liveSettings)
{
    // Null when no host registered live settings (a bare AddBazaarWireDiscovery): options alone apply.
    private readonly WireDiscoveryLiveSettings? _live = liveSettings.FirstOrDefault();

    private readonly Lazy<Dictionary<string, string>> _digests = new(() => schemas.Names.ToDictionary(
        n => n,
        n => schemas.TryGet(n, out var a) ? Digest(a.Content.Span) : string.Empty,
        StringComparer.Ordinal));

    public IResult Document(HttpContext context)
    {
        var settings = Effective();
        if (Gate(context, settings) is { } refused)
        {
            return refused;
        }

        var info = schemas.Info;
        var digests = _digests.Value;
        var list = new List<WireDiscoverySchema>(schemas.Names.Count);
        foreach (var name in schemas.Names)
        {
            if (schemas.TryGet(name, out var artifact))
            {
                list.Add(new WireDiscoverySchema(name, digests[name], artifact.Content.Length, artifact.ContentType, SchemaUrl(settings, name)));
            }
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new WireDiscoveryDocument(new WireDiscoveryProtocol(info.Major, info.Minor), info.Hash, catalog.Transports, catalog.Codecs, list),
            WireDiscoveryJson.Default.WireDiscoveryDocument);
        return Respond(context, settings, body, "application/json", Digest(body));
    }

    public IResult Schema(HttpContext context, string name)
    {
        var settings = Effective();
        if (Gate(context, settings) is { } refused)
        {
            return refused;
        }

        if (!schemas.TryGet(name, out var artifact) || !_digests.Value.TryGetValue(name, out var digest))
        {
            return Results.NotFound();
        }

        return Respond(context, settings, artifact.Content, artifact.ContentType, digest);
    }

    private WireDiscoveryOptions Effective() => _live is null ? options.CurrentValue : _live.Overlay(options.CurrentValue);

    /// <summary>Disabled gives 404; over the limit gives 429. Null means the request may proceed.</summary>
    private IResult? Gate(HttpContext context, WireDiscoveryOptions settings)
    {
        if (!settings.Enabled)
        {
            return Results.NotFound();
        }

        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (limiter.TryAcquire(key, settings.PermitLimit, settings.Window, settings.MaxTrackedClients, out var retryAfter))
        {
            return null;
        }

        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    private static IResult Respond(HttpContext context, WireDiscoveryOptions settings, ReadOnlyMemory<byte> body, string contentType, string digest)
    {
        var headers = context.Response.Headers;
        var etag = new EntityTagHeaderValue("\"" + digest + "\"");
        headers.ETag = etag.ToString();
        headers.CacheControl = "public, max-age=" + settings.CacheMaxAgeSeconds.ToString(CultureInfo.InvariantCulture);
        headers.XContentTypeOptions = "nosniff";
        if (Matches(context.Request.GetTypedHeaders().IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(body.ToArray(), contentType);
    }

    private static bool Matches(IList<EntityTagHeaderValue> ifNoneMatch, EntityTagHeaderValue etag)
    {
        foreach (var candidate in ifNoneMatch)
        {
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    private static string SchemaUrl(WireDiscoveryOptions settings, string name) =>
        settings.BasePath + "/schema/" + string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    private static string Digest(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
