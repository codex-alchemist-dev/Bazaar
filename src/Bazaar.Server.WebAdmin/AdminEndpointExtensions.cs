using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin;

public static class AdminEndpointExtensions
{
    /// <summary>The key under which an allowed request carries its <see cref="AdminSession"/> in <see cref="HttpContext.Items"/>.</summary>
    public const string SessionItemKey = "Bazaar.WebAdmin.Session";

    /// <summary>Puts every endpoint of the builder behind <see cref="IAdminGate"/> for <paramref name="area"/>: 401 without a session, 403 without the capability.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder, AdminArea area)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var gate = http.RequestServices.GetService(typeof(IAdminGate)) as IAdminGate;
            var access = gate is null
                ? new AdminAccess(AdminDecision.Unauthenticated, null)
                : await gate.CheckAsync(http, area, http.RequestAborted);
            switch (access.Decision)
            {
                case AdminDecision.Allowed:
                    http.Items[SessionItemKey] = access.Session;
                    return await next(context);
                case AdminDecision.Forbidden:
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                default:
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
        });
}
