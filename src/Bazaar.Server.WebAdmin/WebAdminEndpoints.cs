using Bazaar.Server.WebAdmin.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Bazaar.Server.WebAdmin;

/// <summary>Maps the admin panel under <c>/admin</c> (doc 51 section 5). Every method and path under it is handled here, so any other method is a 405.</summary>
public static class WebAdminEndpoints
{
    public static IEndpointRouteBuilder MapBazaarWebAdmin(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Map("/admin/{**rest}", AdminRouter.HandleAsync).WithDisplayName("bazaar-web-admin");
        return routes;
    }
}
