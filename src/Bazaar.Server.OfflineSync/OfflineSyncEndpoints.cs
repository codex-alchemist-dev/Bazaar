using Bazaar.OfflineSync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync;

/// <summary>Route mapping for the offline sync module. Host composition calls it once, next to the other route maps.</summary>
public static class OfflineSyncEndpoints
{
    /// <summary>Maps <c>POST /offline-sync/batch</c>. Answers 503 until the component is registered and enabled.</summary>
    public static IEndpointRouteBuilder MapBazaarOfflineSync(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/" + OfflineSyncWire.BatchRoute, static async http =>
        {
            var handler = http.RequestServices.GetService<OfflineSyncBatchHandler>();
            var result = handler is null
                ? Results.Json(new Dictionary<string, string> { ["error"] = "unavailable" }, OfflineSyncErrorContext.Default.DictionaryStringString, statusCode: StatusCodes.Status503ServiceUnavailable)
                : await handler.HandleAsync(http).ConfigureAwait(false);
            await result.ExecuteAsync(http).ConfigureAwait(false);
        });
        return routes;
    }
}
