using System.Globalization;
using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// <c>POST /company/interrupts</c> (doc 24 section 5, read routes): the interrupt-stack pane model as JSON,
/// <c>{ agents: [{agentId, name, frames: [{id, source, reason, state, pushedAt}]}] }</c>.
/// The body is <c>{}</c> (an empty body counts as the same); any other JSON or a body that is not an object answers 400.
/// </summary>
/// <remarks>
/// The guard runs first, and a non-null guard result is returned as it is: the host passes its guarded HTTP surface check
/// (bearer, rate bucket) here, so this module never references Bazaar.Server. Only agents with a live stack are listed, each
/// with its live frames top first. A name comes from the Agents module, and an agent it does not list shows its id. Without a
/// registered <see cref="IInterruptStackReader"/> (the host has not wired the stack service yet) the model lists no agents.
/// Frame reasons are personal text: they are opened only for the <see cref="InterruptViewer"/> the host registers for the
/// request (the company owner, or the agent for its own frames), and everyone else, including a request with no registered viewer,
/// gets <see cref="InterruptVisibilityRules.RedactedReason"/> in their place. Nothing here logs a reason.
/// </remarks>
public static class InterruptsReadRoute
{
    /// <summary>Route path.</summary>
    public const string Path = "/company/interrupts";

    private const int _maxBodyBytes = 4096;

    /// <summary>Maps the route onto <paramref name="routes"/>. Host composition calls it next to the other HTTP routes.</summary>
    public static IEndpointRouteBuilder MapCompanyInterrupts(this IEndpointRouteBuilder routes, Func<HttpContext, ValueTask<IResult?>> guard)
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

    private static async Task<IResult> HandleAsync(HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        var refused = await guard(http);
        if (refused is not null)
        {
            return refused;
        }

        if (!await BodyIsObjectAsync(http.Request, http.RequestAborted))
        {
            return Results.Text("""{"error":"bad_request"}""", "application/json", statusCode: StatusCodes.Status400BadRequest);
        }

        var services = http.RequestServices;
        var ct = http.RequestAborted;
        var reader = services.GetService<IInterruptStackReader>();
        var frames = reader is null ? [] : await reader.ListLiveFramesAsync(ct);
        var names = new Dictionary<Guid, string>();
        if (frames.Count > 0 && services.GetService<IAgentDirectoryReader>() is { } directory)
        {
            foreach (var listing in await directory.ListActiveAgentsAsync(ct))
            {
                names[listing.AgentId] = listing.Name;
            }
        }

        var viewer = services.GetService<InterruptViewer>() ?? InterruptViewer.Anonymous;
        return Results.Text(WriteModel(frames, names, viewer), "application/json");
    }

    private static async Task<bool> BodyIsObjectAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > _maxBodyBytes)
        {
            return false;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _maxBodyBytes)
            {
                return false;
            }
        }

        if (buffer.Length == 0)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the pane model JSON from live frames and the known agent names. Reasons are redacted per <paramref name="viewer"/>.
    /// Public so tests and other heads can check the shape without a server. Agents are listed by name (then id), and each agent's frames keep the order given: top first.
    /// </summary>
    public static string WriteModel(IReadOnlyList<InterruptFrame> frames, IReadOnlyDictionary<Guid, string> names, InterruptViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(viewer);

        var agents = frames
            .Where(f => f.State is InterruptFrameState.Active or InterruptFrameState.Suspended)
            .GroupBy(f => f.AgentId)
            .Select(g => (Id: g.Key, Name: names.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(), Frames: g.ToArray()))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Id);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartArray("agents");
            foreach (var agent in agents)
            {
                json.WriteStartObject();
                json.WriteString("agentId", agent.Id);
                json.WriteString("name", agent.Name);
                json.WriteStartArray("frames");
                foreach (var frame in agent.Frames)
                {
                    json.WriteStartObject();
                    json.WriteString("id", frame.Id);
                    json.WriteString("source", SourceName(frame.Source));
                    json.WriteString("reason", InterruptVisibilityRules.ReasonFor(frame, viewer));
                    json.WriteString("state", StateName(frame.State));
                    json.WriteString("pushedAt", frame.PushedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string SourceName(InterruptSource source) => source switch
    {
        InterruptSource.Owner => "owner",
        InterruptSource.SystemBuild => "system_build",
        InterruptSource.SystemSecurity => "system_security",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static string StateName(InterruptFrameState state) => state switch
    {
        InterruptFrameState.Active => "active",
        InterruptFrameState.Suspended => "suspended",
        InterruptFrameState.Done => "done",
        InterruptFrameState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
