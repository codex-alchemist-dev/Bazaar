using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Calendar;

/// <summary>
/// <c>POST /company/calendar</c> (doc 24 section 5, read routes): the Calendar pane model as JSON,
/// <c>{ displayTimezone: "viewer"|"agent", events: [{id, kind, title, startsAt, endsAt, allDay, tz, agentId|null, projectId|null,
/// affectsAvailability, status}] }</c>. The body is <c>{}</c> for the default window (the current UTC month and the month either
/// side), or an object with optional <c>from</c> and <c>to</c> dates (<c>yyyy-MM-dd</c>, both inclusive). A missing bound keeps the
/// default window's bound. A bad body, a bad date, <c>from</c> after <c>to</c>, or a window over <see cref="MaxWindowDays"/> days
/// answers 400.
/// </summary>
/// <remarks>
/// The guard runs first, and a non-null guard result is returned as it is: the host passes its guarded HTTP surface check
/// (bearer, rate bucket) here, so this module never references Bazaar.Server. Cancelled events are listed (the view strikes them
/// through). Titles are personal fields (C01): they are opened server-side and sent only in this response, never logged. A title
/// whose author key was erased reads as <see cref="ErasedTitle"/>. The route has no caller identity, so it cannot narrow a title
/// to its viewer; per-viewer filtering waits for a guard that hands the principal over.
/// </remarks>
public static class CalendarReadRoute
{
    /// <summary>Route path.</summary>
    public const string Path = "/company/calendar";

    /// <summary>The longest window one call may ask for.</summary>
    public const int MaxWindowDays = 800;

    /// <summary>Title sent when the author's key was erased and the sealed title cannot be opened.</summary>
    public const string ErasedTitle = "(erased)";

    private const int _maxBodyBytes = 4096;

    /// <summary>Maps the route onto <paramref name="routes"/>. Host composition calls it next to the other HTTP routes.</summary>
    public static IEndpointRouteBuilder MapCompanyCalendar(this IEndpointRouteBuilder routes, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guard);

        // A RequestDelegate with the POST method metadata keeps the route free of reflection, so the module stays AOT-safe.
        RequestDelegate handler = async http =>
        {
            var result = await HandleAsync(http, guard);
            await result.ExecuteAsync(http);
        };
        routes.Map(Path, handler).WithMetadata(new HttpMethodMetadata(["POST"]));
        return routes;
    }

    /// <summary>
    /// The window a request covers, as [<paramref name="from"/>, <paramref name="until"/>) instants. <paramref name="fromText"/> and
    /// <paramref name="toText"/> are optional inclusive <c>yyyy-MM-dd</c> dates. Returns false when a date is malformed, the
    /// range is inverted, or it is longer than <see cref="MaxWindowDays"/> days.
    /// </summary>
    public static bool TryResolveWindow(DateTimeOffset now, string? fromText, string? toText, out DateTimeOffset from, out DateTimeOffset until)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var defaultFrom = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var defaultTo = new DateOnly(today.Year, today.Month, 1).AddMonths(2).AddDays(-1);

        var first = defaultFrom;
        var last = defaultTo;
        from = until = default;
        if (fromText is not null && !DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
        {
            return false;
        }

        if (toText is not null && !DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last))
        {
            return false;
        }

        if (last < first || last.DayNumber - first.DayNumber + 1 > MaxWindowDays)
        {
            return false;
        }

        from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        until = new DateTimeOffset(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return true;
    }

    private static async Task<IResult> HandleAsync(HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        var refused = await guard(http);
        if (refused is not null)
        {
            return refused;
        }

        var ct = http.RequestAborted;
        var body = await ReadBodyAsync(http.Request, ct);
        if (body is null)
        {
            return BadRequest();
        }

        var services = http.RequestServices;
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (!TryResolveWindow(now, body.Value.From, body.Value.To, out var from, out var until))
        {
            return BadRequest();
        }

        var calendar = services.GetRequiredService<ICalendarService>();
        var records = await calendar.ListOverlappingAsync(from, until, agentId: null, ct);
        var mode = services.GetService<CalendarReadOptions>()?.DisplayTimezone ?? DisplayTimezone.Viewer;

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("displayTimezone", mode == DisplayTimezone.Agent ? "agent" : "viewer");
            json.WriteStartArray("events");
            foreach (var record in records)
            {
                var title = await calendar.OpenTitleAsync(record, ct) ?? ErasedTitle;
                WriteEvent(json, record, title);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Results.Text(System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length), "application/json");
    }

    private static void WriteEvent(Utf8JsonWriter json, CalendarEventRecord record, string title)
    {
        json.WriteStartObject();
        json.WriteString("id", record.Id);
        json.WriteString("kind", record.Kind.ToString().ToLowerInvariant());
        json.WriteString("title", title);

        // An all-day event goes out as the calendar dates it was written with, both inclusive (the stored end is exclusive).
        // A timed event goes out as UTC instants; the client places it in its own zone.
        if (record.AllDay)
        {
            var zone = ZoneFor(record.Tz);
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.StartsAt, zone).DateTime);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.EndsAt, zone).DateTime).AddDays(-1);
            if (last < first)
            {
                last = first;
            }

            json.WriteString("startsAt", first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteString("endsAt", last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            json.WriteString("startsAt", record.StartsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("endsAt", record.EndsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        json.WriteBoolean("allDay", record.AllDay);
        json.WriteString("tz", record.Tz);
        WriteOptionalGuid(json, "agentId", record.AgentId);
        WriteOptionalGuid(json, "projectId", record.ProjectId);
        json.WriteBoolean("affectsAvailability", record.AffectsAvailability);
        json.WriteString("status", record.Status.ToString().ToLowerInvariant());
        json.WriteEndObject();
    }

    private static void WriteOptionalGuid(Utf8JsonWriter json, string name, Guid? value)
    {
        if (value is { } id)
        {
            json.WriteString(name, id);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static TimeZoneInfo ZoneFor(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Reads the optional <c>from</c> and <c>to</c> of the body. Null means the body is not an acceptable object.</summary>
    private static async Task<(string? From, string? To)?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
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

        if (buffer.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryReadDate(root, "from", out var from) || !TryReadDate(root, "to", out var to))
            {
                return null;
            }

            return (from, to);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadDate(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static IResult BadRequest() =>
        Results.Text("""{"error":"bad_request"}""", "application/json", statusCode: StatusCodes.Status400BadRequest);
}
