using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// The owner producer (doc 24 section 3, "Owner producer"): <c>POST /company/interrupts/push</c> and
/// <c>POST /company/interrupts/pop</c>. Owner only: after the host guard passes, the request's <see cref="InterruptViewer"/> must be
/// the company owner, and anyone else (an agent, an unresolved caller) gets 403. Only an authenticated owner request reaches the
/// stack, so untrusted text can never push (C06). A third push answers 409 <c>depth_exceeded</c>; a pop is idempotent by frame id.
/// </summary>
/// <remarks>
/// The guard is the host's (bearer, rate bucket) and must also register the request's <see cref="InterruptViewer"/>, as the read
/// route's guard does, so this module never references Bazaar.Server. Reasons are personal text and are never logged or echoed.
/// </remarks>
public static class InterruptsWriteRoute
{
    /// <summary>Push route path.</summary>
    public const string PushPath = "/company/interrupts/push";

    /// <summary>Pop route path.</summary>
    public const string PopPath = "/company/interrupts/pop";

    /// <summary>Longest accepted <c>ttlSeconds</c>: seven days.</summary>
    public const int MaxTtlSeconds = 604_800;

    private const int _maxBodyBytes = 4096;

    /// <summary>Maps both routes onto <paramref name="routes"/>. Host composition calls it next to the read route.</summary>
    public static IEndpointRouteBuilder MapCompanyInterruptsWrite(this IEndpointRouteBuilder routes, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guard);

        RequestDelegate push = async http => await (await HandleAsync(http, guard, PushAsync)).ExecuteAsync(http);
        RequestDelegate pop = async http => await (await HandleAsync(http, guard, PopAsync)).ExecuteAsync(http);
        routes.Map(PushPath, push).WithMetadata(new HttpMethodMetadata(["POST"]));
        routes.Map(PopPath, pop).WithMetadata(new HttpMethodMetadata(["POST"]));
        return routes;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard, Func<HttpContext, JsonElement, Guid, Task<IResult>> action)
    {
        var refused = await guard(http);
        if (refused is not null)
        {
            return refused;
        }

        var viewer = http.RequestServices.GetService<InterruptViewer>() ?? InterruptViewer.Anonymous;
        if (!viewer.IsCompanyOwner || viewer.Id == Guid.Empty)
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }

        if (http.RequestServices.GetService<IInterruptStackService>() is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        var body = await ReadObjectAsync(http.Request, http.RequestAborted);
        return body is null ? Error(StatusCodes.Status400BadRequest, "bad_request") : await action(http, body.Value, viewer.Id);
    }

    private static async Task<IResult> PushAsync(HttpContext http, JsonElement body, Guid ownerId)
    {
        if (!TryGuid(body, "agentId", out var agentId) || !TryGuid(body, "gcId", out var gcId)
            || !body.TryGetProperty("reason", out var reasonElement) || reasonElement.ValueKind != JsonValueKind.String)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var reason = reasonElement.GetString()!;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > InterruptStackService.MaxReasonLength)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var emergency = false;
        if (body.TryGetProperty("emergency", out var emergencyElement))
        {
            if (emergencyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }

            emergency = emergencyElement.ValueKind == JsonValueKind.True;
        }

        DateTimeOffset? ttlAt = null;
        if (body.TryGetProperty("ttlSeconds", out var ttlElement) && ttlElement.ValueKind != JsonValueKind.Null)
        {
            if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out var seconds) || seconds is < 1 or > MaxTtlSeconds)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            }

            ttlAt = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().AddSeconds(seconds);
        }

        var service = http.RequestServices.GetRequiredService<IInterruptStackService>();
        try
        {
            var frame = await service.PushAsync(agentId, ownerId, InterruptSource.Owner, reason, gcId, ttlAt, emergency, http.RequestAborted);
            var bypass = InterruptWakePolicy.BypassesVacation(InterruptSource.Owner, emergency);
            return Json($$"""{"frame":{{FrameJson(frame)}},"wakeBypassesVacation":{{(bypass ? "true" : "false")}}}""");
        }
        catch (InterruptDepthExceededException)
        {
            return Error(StatusCodes.Status409Conflict, "depth_exceeded");
        }
    }

    private static async Task<IResult> PopAsync(HttpContext http, JsonElement body, Guid ownerId)
    {
        if (!TryGuid(body, "agentId", out var agentId) || !TryGuid(body, "frameId", out var frameId))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var service = http.RequestServices.GetRequiredService<IInterruptStackService>();
        try
        {
            var resumed = await service.PopAsync(agentId, frameId, http.RequestAborted);
            return Json($$"""{"resumed":{{(resumed is null ? "null" : FrameJson(resumed))}}}""");
        }
        catch (InterruptFrameNotTopException)
        {
            return Error(StatusCodes.Status409Conflict, "not_top");
        }
    }

    private static string FrameJson(InterruptFrame frame) =>
        $$"""{"id":"{{frame.Id}}","agentId":"{{frame.AgentId}}","source":"{{SourceName(frame.Source)}}","state":"{{frame.State.ToString().ToLowerInvariant()}}","pushedAt":"{{frame.PushedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}}","emergency":{{(frame.Emergency ? "true" : "false")}}}""";

    private static string SourceName(InterruptSource source) => source switch
    {
        InterruptSource.Owner => "owner",
        InterruptSource.SystemBuild => "system_build",
        _ => "system_security",
    };

    private static bool TryGuid(JsonElement body, string name, out Guid value)
    {
        value = Guid.Empty;
        return body.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value) && value != Guid.Empty;
    }

    private static async Task<JsonElement?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > _maxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _maxBodyBytes)
            {
                return null;
            }
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Json(string json) => Results.Text(json, "application/json");

    private static IResult Error(int status, string code) => Results.Text($$"""{"error":"{{code}}"}""", "application/json", statusCode: status);
}
