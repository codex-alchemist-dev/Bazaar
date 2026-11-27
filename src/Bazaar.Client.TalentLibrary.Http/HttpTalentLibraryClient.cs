using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.TalentLibrary.Http;

/// <summary>Source-generated JSON for the Talent library wire types.</summary>
[JsonSerializable(typeof(LibraryEntry[]))]
[JsonSerializable(typeof(LibraryVersion[]))]
[JsonSerializable(typeof(LibraryDiff))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class TalentLibraryJsonContext : JsonSerializerContext;

/// <summary>
/// Reads the Talent library from the server (docs/design/25; routes PROPOSED): <c>GET talent/templates</c>,
/// <c>GET talent/templates/{id}/versions</c> and <c>GET talent/templates/{id}/diff?from=&amp;to=</c>. Read only. A 404 means
/// unknown (empty or null); any other failure throws.
/// </summary>
public sealed class HttpTalentLibraryClient(HttpClient http, Func<string?> credential) : ITalentLibrarySource
{
    /// <summary>Creates a client bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpTalentLibraryClient Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpTalentLibraryClient(client, () => endpoint.Credential);
    }

    public async ValueTask<IReadOnlyList<LibraryEntry>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync("talent/templates", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(TalentLibraryJsonContext.Default.LibraryEntryArray, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async ValueTask<IReadOnlyList<LibraryVersion>> VersionsAsync(string templateId, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"talent/templates/{Uri.EscapeDataString(templateId)}/versions", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(TalentLibraryJsonContext.Default.LibraryVersionArray, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async ValueTask<LibraryDiff?> DiffAsync(string templateId, int olderVersion, int newerVersion, CancellationToken cancellationToken)
    {
        var route = string.Create(CultureInfo.InvariantCulture, $"talent/templates/{Uri.EscapeDataString(templateId)}/diff?from={olderVersion}&to={newerVersion}");
        using var response = await GetAsync(route, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(TalentLibraryJsonContext.Default.LibraryDiff, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> GetAsync(string route, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (credential() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
