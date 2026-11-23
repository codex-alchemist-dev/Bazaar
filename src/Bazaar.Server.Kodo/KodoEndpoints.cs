using System.Text.Json;
using Bazaar.Contracts.Kodo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The Kodo read API under <c>/api/kodo/v1/</c> (doc 46 section 15.1), read-only. Host composition passes the HTTP surface guard
/// (bearer, role, rate bucket) as <c>guard</c>, so this module never references Bazaar.Server. Every route is mapped through it.
/// </summary>
/// <remarks>
/// Redaction (section 15): bodies carry probe ids, wire statuses, cause codes and hashes only. Exception text, file paths and journal
/// payload bytes never leave the process. A request id is checked against a strict pattern before it is used.
/// </remarks>
public static class KodoEndpoints
{
    public const string Prefix = "/api/kodo/v1";

    /// <summary>The newest-first journal page size when the caller sends none.</summary>
    public const int DefaultEventLimit = 50;

    public static IEndpointRouteBuilder MapBazaarKodo(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapGet(Prefix + "/status", (IKodoStatusSource source, HttpContext http) =>
        {
            if (Gate(source, http) is { } refused)
            {
                return refused;
            }

            return source.Latest is { } snapshot
                ? Results.Bytes(KodoWireCodec.EncodeSnapshot(snapshot), "application/json")
                : Error(http, StatusCodes.Status503ServiceUnavailable, "not_ready", "Kodo has not completed its first pass.", retryable: true);
        }));

        guard(app.MapGet(Prefix + "/events", (IKodoStatusSource source, HttpContext http, string? limit) =>
        {
            if (Gate(source, http) is { } refused)
            {
                return refused;
            }

            int count = DefaultEventLimit;
            if (limit is not null && (!int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count) || count is < 1 or > 200))
            {
                return Error(http, StatusCodes.Status400BadRequest, "bad_limit", "limit must be a whole number from 1 to 200.", retryable: false);
            }

            IReadOnlyList<KodoJournalEntryView> entries = source.RecentJournal(count);
            return Json(new KodoEventsResponse(1, entries.Count == 0 ? null : entries[0].Hash, entries), KodoJsonContext.Default.KodoEventsResponse);
        }));

        guard(app.MapGet(Prefix + "/journal/proof/{sequence}", (IKodoStatusSource source, HttpContext http, string sequence) =>
        {
            if (Gate(source, http) is { } refused)
            {
                return refused;
            }

            if (!long.TryParse(sequence, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long number))
            {
                return Error(http, StatusCodes.Status400BadRequest, "bad_sequence", "sequence must be a whole number.", retryable: false);
            }

            return source.JournalProof(number) is { } proof
                ? Json(new KodoProofResponse(1, proof), KodoJsonContext.Default.KodoProofResponse)
                : Error(http, StatusCodes.Status404NotFound, "proof_unavailable", "No proof is available for that record.", retryable: false);
        }));

        return app;
    }

    private static IResult? Gate(IKodoStatusSource source, HttpContext http) =>
        source.Enabled ? null : Error(http, StatusCodes.Status404NotFound, "kodo_disabled", "Kodo is switched off on this server.", retryable: false);

    private static IResult Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Results.Text(JsonSerializer.Serialize(value, info), "application/json");

    private static IResult Error(HttpContext http, int status, string code, string message, bool retryable) =>
        Results.Text(
            JsonSerializer.Serialize(new KodoErrorResponse(code, message, retryable, http.TraceIdentifier), KodoJsonContext.Default.KodoErrorResponse),
            "application/json",
            statusCode: status);
}
