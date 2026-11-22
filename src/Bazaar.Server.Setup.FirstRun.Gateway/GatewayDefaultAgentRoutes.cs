using System.Text;
using Bazaar.Server.Setup.FirstRun;

namespace Bazaar.Server.Setup.FirstRun.Gateway;

/// <summary>
/// The default agent's routes (doc 45 section 1.15, doc 05 "Claude lane"). The whitelist is the Claude-family models of the
/// gateway's enabled sources, provider-qualified, plus one priority route over them (native source first, then the rest in
/// directory order). <c>defaultAgentSources</c> narrows it to those source ids; a source the gateway does not list is ignored,
/// so the agent never reaches a source that is not there. With no Claude source the result is empty and no route is written.
/// </summary>
public sealed class GatewayDefaultAgentRoutes(IClaudeSourceDirectory directory) : IDefaultAgentRoutes
{
    /// <summary>The route name prefix; the profile is appended, so each profile gets its own route.</summary>
    public const string RoutePrefix = "default-agent";

    /// <summary>The route id for <paramref name="profile"/>: letters, digits and dashes only, so it is always a valid route name.</summary>
    public static string RouteIdFor(string profile)
    {
        var sb = new StringBuilder(RoutePrefix);
        var slug = new StringBuilder();
        foreach (var c in profile ?? string.Empty)
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > 64) text = text[..64].Trim('-');
        if (text.Length > 0) sb.Append('-').Append(text);
        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetWhitelistAsync(IReadOnlyList<string> requestedSources, string profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedSources);

        var wanted = requestedSources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var models = (await directory.ListClaudeModelsAsync(cancellationToken))
            .Where(m => wanted.Count == 0 || wanted.Contains(m.Model.ProviderId))
            .DistinctBy(m => m.Model)
            .OrderByDescending(m => m.Native)
            .ToList();
        if (models.Count == 0) return [];

        var routeId = RouteIdFor(profile);
        await directory.UpsertPriorityRouteAsync(routeId, models.Select(m => m.Model).ToList(), cancellationToken);
        return [routeId, .. models.Select(m => m.Model.ToString())];
    }
}
