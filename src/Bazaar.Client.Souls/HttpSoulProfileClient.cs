using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Contracts.Soul;

namespace Bazaar.Client.Souls;

/// <summary>An agent's profile card fields and version chain, as the profile route answers them.</summary>
public sealed record AgentProfile(SoulProfile Profile, IReadOnlyList<SoulVersion> Versions);

/// <summary>
/// Reads <c>GET /agents/{agent_id}/profile</c> (doc 07, PROPOSED). The caller builds the <see cref="HttpClient"/> from the server
/// endpoint, so the key-hash pin and the loopback rule already apply. The bearer token is never logged. One 404 covers every
/// reason the server will not show a profile, so a miss reads as null.
/// </summary>
public sealed class HttpSoulProfileClient(HttpClient http, Func<Task<string?>> bearerToken)
{
    /// <summary>Returns the profile, or null when the server has none to show this person. Throws when the server cannot be reached or refuses the token.</summary>
    public async Task<AgentProfile?> GetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"agents/{agentId}/profile");
        if (await bearerToken().ConfigureAwait(false) is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var reply = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.StatusCode == HttpStatusCode.NotFound) return null;
        reply.EnsureSuccessStatusCode();

        ProfileBody? body;
        try
        {
            body = await reply.Content.ReadFromJsonAsync(SoulProfileJsonContext.Default.ProfileBody, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is null) return null;
        var versions = (body.Versions ?? []).Select(v => new SoulVersion(
            body.SoulId, v.Version, v.ParentVersion, v.CharterSha256 ?? "", v.PreferencesJson ?? "{}", v.ContentSha256 ?? "",
            v.Author ?? "", v.Reason ?? "", v.DecisionRef, v.CreatedAt)).ToList();
        return new AgentProfile(new SoulProfile(body.SoulId, body.Name ?? "", body.Role ?? "", body.ModelBadge ?? ""), versions);
    }

    internal sealed record VersionBody(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("parent_version")] int? ParentVersion,
        [property: JsonPropertyName("charter_sha256")] string? CharterSha256,
        [property: JsonPropertyName("preferences_json")] string? PreferencesJson,
        [property: JsonPropertyName("content_sha256")] string? ContentSha256,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("decision_ref")] string? DecisionRef,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    internal sealed record ProfileBody(
        [property: JsonPropertyName("soul_id")] Guid SoulId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("model_badge")] string? ModelBadge,
        [property: JsonPropertyName("versions")] VersionBody[]? Versions);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(HttpSoulProfileClient.ProfileBody))]
internal sealed partial class SoulProfileJsonContext : JsonSerializerContext;
