using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Core.Vault;

/// <summary>One card from <c>GET /vault/search</c>. Field names follow the server's snake_case JSON.</summary>
public sealed record VaultCardDto(
    Guid DocId,
    int Ordinal,
    string Path,
    string Title,
    string Type,
    string? Project,
    string[] HeadingPath,
    string Basis,
    double Score,
    string Gist,
    int ApproxTokens);

/// <summary>The search response: cards plus whether the answer was lexical only, and how fresh the index is.</summary>
public sealed record VaultSearchResponseDto(VaultCardDto[] Cards, bool LexicalOnly, string? IndexedCommit);

/// <summary>A body response from <c>GET /vault/doc</c>.</summary>
public sealed record VaultBodyDto(string Body);

/// <summary>A non-success answer from the Vault HTTP surface. The status is the only detail the client shows.</summary>
public sealed class VaultApiException(HttpStatusCode status) : Exception($"Vault request failed with HTTP {(int)status}.")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Client for the Vault read routes (doc 02 authenticated surface). The head sets the base address on the
/// <see cref="HttpClient"/> and supplies the bearer credential, which is read per request so a re-pair takes effect at once.
/// </summary>
public sealed class VaultApi(HttpClient http, Func<string?> credential)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Searches one Vault. Access is decided by the server, so the client sends no identity of its own.</summary>
    public async Task<VaultSearchResponseDto> SearchAsync(
        Guid vaultId, string text, string? project, string? type, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var url = $"vault/search?vault={vaultId:D}&q={Uri.EscapeDataString(text)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(project)) url += "&project=" + Uri.EscapeDataString(project);
        if (!string.IsNullOrEmpty(type)) url += "&type=" + Uri.EscapeDataString(type);

        var json = await GetAsync(url, cancellationToken);
        return JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultSearchResponseDto)
            ?? throw new VaultApiException(HttpStatusCode.BadGateway);
    }

    /// <summary>Fetches one block's body. A document the caller cannot read answers <see cref="HttpStatusCode.NotFound"/>.</summary>
    public async Task<string> FetchBodyAsync(Guid vaultId, Guid docId, int ordinal, CancellationToken cancellationToken)
    {
        var url = $"vault/doc?vault={vaultId:D}&doc={docId:D}&ordinal={ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var json = await GetAsync(url, cancellationToken);
        var body = JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultBodyDto);
        return body?.Body ?? throw new VaultApiException(HttpStatusCode.BadGateway);
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = _credential();
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new VaultApiException(response.StatusCode);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

/// <summary>Source-generated JSON for the Vault responses (snake_case, no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(VaultSearchResponseDto))]
[JsonSerializable(typeof(VaultBodyDto))]
internal sealed partial class VaultJsonContext : JsonSerializerContext
{
}
