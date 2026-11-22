using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Org;

/// <summary>
/// <c>POST /company/org</c> (doc 24 section 5, read routes): the Org pane model as JSON,
/// <c>{ projects: [{id, name}], agents: [{id, name}], edges: [{id, kind, fromAgentId, toAgentId, projectId|null, startsAt, endsAt|null}] }</c>.
/// The body is <c>{}</c> (an empty body counts as the same); any other JSON or a body that is not an object answers 400.
/// </summary>
/// <remarks>
/// The guard runs first, and a non-null guard result is returned as it is: the host passes its guarded HTTP surface check
/// (bearer, rate bucket) here, so this module never references Bazaar.Server. Edges include ended relations, so the view can
/// show ended loans dimmed. Edges grant no authority, so every authenticated caller sees the same graph.
/// </remarks>
public static class OrgReadRoute
{
    /// <summary>Route path.</summary>
    public const string Path = "/company/org";

    private const int _maxBodyBytes = 4096;

    /// <summary>Maps the route onto <paramref name="routes"/>. Host composition calls it next to the other HTTP routes.</summary>
    public static IEndpointRouteBuilder MapCompanyOrg(this IEndpointRouteBuilder routes, Func<HttpContext, ValueTask<IResult?>> guard)
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
        var relations = await services.GetRequiredService<IOrgRelationReader>().ListAllAsync(includeEnded: true, ct);
        var directory = services.GetService<IOrgDirectory>();
        var projects = directory is null ? [] : await directory.ListProjectsAsync(ct);
        var agents = directory is null ? [] : await directory.ListAgentsAsync(ct);
        return Results.Text(WriteModel(projects, agents, relations), "application/json");
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

    /// <summary>Builds the pane model JSON. Public so tests and other heads can check the shape without a server.</summary>
    public static string WriteModel(
        IReadOnlyList<OrgDirectoryEntry> projects,
        IReadOnlyList<OrgDirectoryEntry> agents,
        IReadOnlyList<OrgRelation> relations)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(relations);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            WriteEntries(json, "projects", projects);
            WriteEntries(json, "agents", agents);

            json.WriteStartArray("edges");
            foreach (var relation in relations)
            {
                json.WriteStartObject();
                json.WriteString("id", relation.Id);
                json.WriteString("kind", relation.Kind == OrgRelationKind.ReportsTo ? "reports_to" : "lent_to");
                json.WriteString("fromAgentId", relation.FromAgentId);
                json.WriteString("toAgentId", relation.ToAgentId);
                if (relation.ProjectId is { } projectId)
                {
                    json.WriteString("projectId", projectId);
                }
                else
                {
                    json.WriteNull("projectId");
                }

                json.WriteString("startsAt", relation.StartsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                if (relation.EndsAt is { } endsAt)
                {
                    json.WriteString("endsAt", endsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                }
                else
                {
                    json.WriteNull("endsAt");
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteEntries(Utf8JsonWriter json, string name, IReadOnlyList<OrgDirectoryEntry> entries)
    {
        json.WriteStartArray(name);
        foreach (var entry in entries)
        {
            json.WriteStartObject();
            json.WriteString("id", entry.Id);
            json.WriteString("name", entry.Name);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }
}
