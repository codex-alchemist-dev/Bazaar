using System.Text.Json;
using Bazaar.OfflineSync;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.OfflineSync;

/// <summary>
/// <c>POST /offline-sync/batch</c> (doc 56 step 3): authenticate, bind the batch to the authenticated device, run the
/// <see cref="OfflineSyncProcessor"/> and answer its result. Errors never carry internal detail.
/// </summary>
public sealed class OfflineSyncBatchHandler(IServiceProvider services, OfflineSyncProcessor processor)
{
    /// <summary>Largest request body accepted, in bytes.</summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>Most writes accepted in one batch.</summary>
    public const int MaxWritesPerBatch = 1000;

    /// <summary>Handles one request.</summary>
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var authenticator = services.GetService<IOfflineSyncRequestAuthenticator>();
        if (authenticator is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        var auth = await authenticator.AuthenticateAsync(http, http.RequestAborted).ConfigureAwait(false);
        if (auth.Caller is not { } caller)
        {
            return Error(auth.FailureStatus, auth.FailureCode);
        }

        var length = http.Request.ContentLength;
        if (length is null or < 1)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        if (length > MaxBodyBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        }

        OfflineBatchRequestDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(http.Request.Body, OfflineSyncWireContext.Default.OfflineBatchRequestDto, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var batch = OfflineSyncWire.ToBatch(body);
        if (batch is null || batch.Writes.Count > MaxWritesPerBatch || batch.Writes.Any(w => w.DeviceId != batch.DeviceId))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        // The device is the one the credential was issued to, never the one the body claims.
        if (batch.DeviceId != caller.DeviceId)
        {
            return Error(StatusCodes.Status403Forbidden, "device_mismatch");
        }

        var result = await processor.ProcessAsync(batch, http.RequestAborted).ConfigureAwait(false);
        return Results.Json(OfflineSyncWire.ToDto(result), OfflineSyncWireContext.Default.OfflineBatchResponseDto);
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new Dictionary<string, string> { ["error"] = code }, OfflineSyncErrorContext.Default.DictionaryStringString, statusCode: status);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class OfflineSyncErrorContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
