using System.Globalization;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Http;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Http;

/// <summary>
/// <c>POST /blobs/token-amounts</c>: the digit-stream intake of doc 48, DECIDED 11.4 ("Transport above the frame size"), on the
/// authenticated HTTP surface next to the blob upload (doc 02). The body is a plain decimal of any length; the response is the
/// id of the sealed large amount, which a window limit setting may refer to ("Limits as large amounts").
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Access: the blob upload's guard (transport rule, bearer credential, HTTP rate bucket), then the owner principal only,
/// because only the operator changes the <c>agent.budget</c> settings (doc 48 section 11 settings table). Others get 403.</item>
/// <item>Limits: <c>agent.budget.amount_intake_max_digits</c> is enforced while the body is read (413 at the first digit beyond
/// it, and at once when <c>Content-Length</c> is already longer than the guard allows). Malformed text is 400. Zero or an amount
/// that fits the inline tier is 422. The server's body size cap is raised to the guard's text length for this request only.</item>
/// <item>Result: 201 with <c>{"id", "integer_digits", "fraction_digits"}</c>. Nothing partial is ever sealed.</item>
/// </list>
/// </remarks>
public static class TokenAmountIntakeEndpoints
{
    /// <summary>The route.</summary>
    public const string Route = "/blobs/token-amounts";

    /// <summary>Maps the route. The listener must allow POST on <c>/blobs</c> paths, as for the blob upload.</summary>
    public static IEndpointRouteBuilder MapBazaarTokenAmountIntake(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(Route, async http =>
        {
            var result = await HttpSurfaceEndpoints.Guarded(http, IntakeAsync).ConfigureAwait(false);
            await result.ExecuteAsync(http).ConfigureAwait(false);
        });
        return routes;
    }

    private static async Task<IResult> IntakeAsync(HttpContext http, PrincipalRef principal)
    {
        if (principal.Kind != "owner")
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }

        var services = http.RequestServices;
        var store = services.GetService<PostgresLargeTokenAmountStore>();
        var options = services.GetService<TokenBudgetOptions>();
        if (store is null || options is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        var guard = options.AmountIntakeMaxDigits;
        long? maxBody = guard.IsUnlimited ? null : (long)guard.MaxIntegerDigits + guard.MaxFractionDigits + 1;
        if (maxBody is { } most && http.Request.ContentLength > most)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "over_intake_guard");
        }

        // The guard is the size limit for this body; the server-wide cap would otherwise cut a long amount at its own size.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBody;
        }

        try
        {
            var (id, digits) = await store.IntakeAsync(http.Request.Body, guard, principal.Id, http.RequestAborted).ConfigureAwait(false);
            var body = string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"id":"{{id:D}}","integer_digits":{{digits.IntegerDigits}},"fraction_digits":{{digits.FractionDigits}}}""");
            return Results.Text(body, "application/json", statusCode: StatusCodes.Status201Created);
        }
        catch (TokenAmountIntakeException ex)
        {
            return ex.Failure switch
            {
                TokenAmountIntakeFailure.OverGuard => Error(StatusCodes.Status413PayloadTooLarge, "over_intake_guard"),
                TokenAmountIntakeFailure.FitsInline => Error(StatusCodes.Status422UnprocessableEntity, "fits_inline"),
                _ => Error(StatusCodes.Status400BadRequest, "malformed_amount"),
            };
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The body ran past the guard's text length, which is this request's body size limit, before the digits were read.
            return Error(StatusCodes.Status413PayloadTooLarge, "over_intake_guard");
        }
        catch (IOException)
        {
            // The body ended early or broke (a dropped connection, or more than the size limit); the staged rows are gone.
            return Error(StatusCodes.Status400BadRequest, "body_incomplete");
        }
        catch (NpgsqlException)
        {
            http.Response.Headers.RetryAfter = "30";
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }
    }

    /// <summary>The surface's error shape, <c>{"error": code}</c>, written without reflection.</summary>
    private static IResult Error(int status, string code) =>
        Results.Text($$"""{"error":"{{code}}"}""", "application/json", statusCode: status);
}
