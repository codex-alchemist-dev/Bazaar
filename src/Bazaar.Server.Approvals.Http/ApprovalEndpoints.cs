using System.Text.Json;
using Bazaar.Contracts.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Approvals.Http;

/// <summary>
/// The phone's approval routes (doc 29 Approvals, PROPOSED): <c>GET /approvals/pending</c> and
/// <c>POST /approvals/{id}/decision</c>. Only the owner may call either. The decision route only maps the wire to
/// <see cref="ApprovalService.DecideAsync"/>, which verifies the signature over the stored hash and expiry; nothing here
/// approves anything, and the card is built from the stored row, never from the client.
/// </summary>
public static class ApprovalEndpoints
{
    private const int _maxBodyBytes = 16 * 1024;
    private const int _maxPending = 100;
    private const string _ownerKind = "owner";

    /// <summary>
    /// Maps both routes behind the host's own guard (<c>HttpSurfaceEndpoints.Guarded</c>: plaintext refusal, bearer check, rate
    /// bucket), which runs the route with the signed-in principal. The module then lets only the owner through. Services are
    /// looked up per request, so a missing one answers 503.
    /// </summary>
    public static IEndpointRouteBuilder MapBazaarApprovalRoutes(
        this IEndpointRouteBuilder routes,
        Func<HttpContext, Func<HttpContext, PrincipalRef, Task<IResult>>, Task<IResult>> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        routes.MapGet(ApprovalRoutes.Pending, Route(guard, PendingAsync));
        routes.MapPost(ApprovalRoutes.DecisionTemplate, Route(guard, DecideAsync));
        return routes;
    }

    // A RequestDelegate keeps the routes free of reflection (the project is AOT compatible).
    private static RequestDelegate Route(
        Func<HttpContext, Func<HttpContext, PrincipalRef, Task<IResult>>, Task<IResult>> guard,
        Func<HttpContext, Task<IResult>> run) =>
        async http => await (await guard(http, (h, principal) =>
            string.Equals(principal.Kind, _ownerKind, StringComparison.Ordinal) ? run(h) : Task.FromResult(Status(StatusCodes.Status403Forbidden))).ConfigureAwait(false)).ExecuteAsync(http).ConfigureAwait(false);

    private static async Task<IResult> PendingAsync(HttpContext http)
    {
        var lister = http.RequestServices.GetService<IPendingApprovalLister>();
        if (lister is null)
        {
            return Status(StatusCodes.Status503ServiceUnavailable);
        }

        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var rows = await lister.ListPendingAsync(now, _maxPending, http.RequestAborted).ConfigureAwait(false);
        var cards = rows.Select(r => ApprovalCardMapper.ToCard(r, now)).ToArray();
        return Results.Json(cards, ApprovalContractsJsonContext.Default.ApprovalCardDtoArray);
    }

    private static async Task<IResult> DecideAsync(HttpContext http)
    {
        var service = http.RequestServices.GetService<ApprovalService>();
        if (service is null)
        {
            return Status(StatusCodes.Status503ServiceUnavailable);
        }

        if (http.Request.ContentLength is > _maxBodyBytes || !Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id))
        {
            return Status(StatusCodes.Status400BadRequest);
        }

        var dto = await ReadAsync(http).ConfigureAwait(false);
        if (dto is null || dto.ApprovalId != id)
        {
            return Status(StatusCodes.Status400BadRequest);
        }

        var mapped = ApprovalDecisionMapper.Map(dto);
        if (!mapped.IsAccepted || mapped.Command is null)
        {
            return Status(StatusCodes.Status400BadRequest);
        }

        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var command = mapped.Command;
        var result = await service.DecideAsync(command.ApprovalId, command.Decision, command.DeviceId, command.Signature, now, http.RequestAborted).ConfigureAwait(false);
        return result.Outcome switch
        {
            ApprovalService.DecisionOutcome.Recorded or ApprovalService.DecisionOutcome.AlreadyDecided => Status(StatusCodes.Status204NoContent),
            ApprovalService.DecisionOutcome.NotFound => Status(StatusCodes.Status404NotFound),
            ApprovalService.DecisionOutcome.Expired => Status(StatusCodes.Status410Gone),
            ApprovalService.DecisionOutcome.Conflict => Status(StatusCodes.Status409Conflict),
            ApprovalService.DecisionOutcome.InvalidDeviceId => Status(StatusCodes.Status400BadRequest),
            _ => Status(StatusCodes.Status403Forbidden),
        };
    }

    private static async Task<ApprovalDecisionDto?> ReadAsync(HttpContext http)
    {
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (limited.Length + read > _maxBodyBytes)
                {
                    return null;
                }

                limited.Write(buffer, 0, read);
            }

            limited.Position = 0;
            return await JsonSerializer.DeserializeAsync(limited, ApprovalContractsJsonContext.Default.ApprovalDecisionDto, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Status(int status) => Results.StatusCode(status);
}
