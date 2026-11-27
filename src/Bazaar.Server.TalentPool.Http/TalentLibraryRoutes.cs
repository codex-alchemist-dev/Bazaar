using System.Globalization;
using System.Text.Json.Serialization;
using Bazaar.Server.Council.Hiring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.TalentPool.Http;

/// <summary>One template in the library: its latest version and where that version stands.</summary>
public sealed record LibraryEntryDto(string TemplateId, string Name, int LatestVersion, string Standing, int ToolCount, int VersionCount);

/// <summary>One stored version of a template.</summary>
public sealed record LibraryVersionDto(int Version, string ContentSha, string SubmittedBy, DateTimeOffset SubmittedAt, string Standing);

/// <summary>One prompt line of a diff. <c>Kind</c> is <c>same</c>, <c>added</c> or <c>removed</c>.</summary>
public sealed record LibraryLineDto(string Kind, string Text);

/// <summary>The difference between two versions, as the library view shows it.</summary>
public sealed record LibraryDiffDto(
    string TemplateId,
    int FromVersion,
    int ToVersion,
    string? OldName,
    string? NewName,
    LibraryLineDto[] PromptLines,
    string[] ToolsAdded,
    string[] ToolsRemoved,
    string[] ModelsAdded,
    string[] ModelsRemoved,
    string[] FilesAdded,
    string[] FilesRemoved,
    bool NeedsNewToolGrant);

[JsonSerializable(typeof(LibraryEntryDto[]))]
[JsonSerializable(typeof(LibraryVersionDto[]))]
[JsonSerializable(typeof(LibraryDiffDto))]
internal sealed partial class TalentRoutesJson : JsonSerializerContext;

/// <summary>
/// The Talent library routes (docs/design/25, Talent Pool: library with version diffs; routes PROPOSED). They only read: the
/// stores are read directly, so they work before the host wires <c>ICouncilService</c>. The host passes the HTTP surface guard as
/// <c>guard</c>.
/// <list type="bullet">
/// <item><c>GET /talent/templates</c>: the latest version of every template with its standing.</item>
/// <item><c>GET /talent/templates/{id}/versions</c>: every version, oldest first; 404 for an unknown template.</item>
/// <item><c>GET /talent/templates/{id}/diff?from=&amp;to=</c>: what changed. <c>to</c> defaults to the latest version and
/// <c>from</c> to the version before it; 400 for bad numbers or equal versions; 404 for an unknown version.</item>
/// </list>
/// </summary>
public static class TalentLibraryRoutes
{
    /// <summary>Maps the three routes.</summary>
    public static IEndpointRouteBuilder MapBazaarTalentLibrary(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);
        guard(app.MapGet("/talent/templates", (Func<HttpContext, Task<IResult>>)ListAsync));
        guard(app.MapGet("/talent/templates/{id}/versions", (Func<string, HttpContext, Task<IResult>>)VersionsAsync));
        guard(app.MapGet("/talent/templates/{id}/diff", (Func<string, HttpContext, Task<IResult>>)DiffAsync));
        return app;
    }

    internal static async Task<IResult> ListAsync(HttpContext http)
    {
        var store = http.RequestServices.GetRequiredService<ITalentTemplateStore>();
        var hires = http.RequestServices.GetRequiredService<IHireDecisionStore>();
        var ct = http.RequestAborted;
        var entries = new List<LibraryEntryDto>();
        foreach (var t in await store.ListLatestAsync(ct).ConfigureAwait(false))
        {
            var count = (await store.ListVersionsAsync(t.TemplateId, ct).ConfigureAwait(false)).Count;
            entries.Add(new LibraryEntryDto(t.TemplateId, t.Manifest.Name, t.Version, await StandingAsync(hires, t, ct).ConfigureAwait(false), t.Manifest.ToolAllowlist.Count, count));
        }

        return Results.Json(entries.ToArray(), TalentRoutesJson.Default.LibraryEntryDtoArray);
    }

    internal static async Task<IResult> VersionsAsync(string id, HttpContext http)
    {
        var store = http.RequestServices.GetRequiredService<ITalentTemplateStore>();
        var hires = http.RequestServices.GetRequiredService<IHireDecisionStore>();
        var ct = http.RequestAborted;
        var versions = await store.ListVersionsAsync(id, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        var dtos = new List<LibraryVersionDto>();
        foreach (var t in versions)
        {
            dtos.Add(new LibraryVersionDto(t.Version, t.ContentSha, t.SubmittedBy, t.SubmittedAt, await StandingAsync(hires, t, ct).ConfigureAwait(false)));
        }

        return Results.Json(dtos.ToArray(), TalentRoutesJson.Default.LibraryVersionDtoArray);
    }

    internal static async Task<IResult> DiffAsync(string id, HttpContext http)
    {
        var store = http.RequestServices.GetRequiredService<ITalentTemplateStore>();
        var ct = http.RequestAborted;
        var query = http.Request.Query;
        var versions = await store.ListVersionsAsync(id, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        if (!TryVersion(query["to"], versions[^1].Version, out var to) || !TryVersion(query["from"], to - 1, out var from) || from == to || from < 1)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        var older = versions.FirstOrDefault(t => t.Version == from);
        var newer = versions.FirstOrDefault(t => t.Version == to);
        if (older is null || newer is null)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        var d = TalentVersionDiffer.Diff(older, newer);
        var dto = new LibraryDiffDto(
            d.TemplateId,
            d.FromVersion,
            d.ToVersion,
            d.NameChange?.From,
            d.NameChange?.To,
            [.. d.PromptLines.Select(l => new LibraryLineDto(l.Kind.ToString().ToLowerInvariant(), l.Text))],
            [.. d.ToolsAdded],
            [.. d.ToolsRemoved],
            [.. d.ModelsAdded],
            [.. d.ModelsRemoved],
            [.. d.FilesAdded],
            [.. d.FilesRemoved],
            d.NeedsNewToolGrant);
        return Results.Json(dto, TalentRoutesJson.Default.LibraryDiffDto);
    }

    private static bool TryVersion(string? text, int fallback, out int version)
    {
        if (string.IsNullOrEmpty(text))
        {
            version = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version >= 1;
    }

    private static async Task<string> StandingAsync(IHireDecisionStore hires, TalentTemplate t, CancellationToken ct) =>
        await hires.FindAsync(t.HireKey, ct).ConfigureAwait(false) is { } state
            ? state.Status switch
            {
                HireStatus.Pending => "vote_pending",
                HireStatus.Released => "released",
                _ => "blocked",
            }
            : "quarantined";
}
