using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.ColdArchive;

/// <summary>The archive routes over HTTP with the endpoint's bearer credential. A refusal throws <see cref="ArchiveRequestException"/>.</summary>
public sealed class HttpColdArchiveClient(HttpClient http, Func<string?> credential) : IColdArchiveClient
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Creates a client bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpColdArchiveClient Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpColdArchiveClient(client, () => endpoint.Credential);
    }

    public async Task<IReadOnlyList<ArchiveUnitInfo>> ListAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "archive/units", ct).ConfigureAwait(false);
        return [.. doc.RootElement.GetProperty("units").EnumerateArray().Select(u => new ArchiveUnitInfo(Str(u, "kind"), Str(u, "id")))];
    }

    public async Task<ArchiveOutcome> ArchiveAsync(string kind, string id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, UnitPath(kind, id), ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new ArchiveOutcome(Str(r, "kind"), Str(r, "id"), r.GetProperty("records").GetInt32(), r.GetProperty("segments").GetInt32(), Str(r, "rootHash"));
    }

    public async Task<ArchiveVerifyOutcome> VerifyAsync(string kind, string id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, UnitPath(kind, id) + "/verify", ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var root = r.TryGetProperty("rootHash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        return new ArchiveVerifyOutcome(r.GetProperty("ok").GetBoolean(), Strings(r, "problems"), root);
    }

    public async Task<ArchiveRestoreOutcome> RestoreAsync(string kind, string id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, UnitPath(kind, id) + "/restore", ct).ConfigureAwait(false);
        var r = doc.RootElement;
        return new ArchiveRestoreOutcome(r.GetProperty("restored").GetInt32(), r.GetProperty("alreadyPresent").GetInt32(), Strings(r, "conflicts"));
    }

    public async Task<IReadOnlyList<ArchiveHit>> SearchAsync(string query, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "archive/search?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        return [.. doc.RootElement.GetProperty("hits").EnumerateArray().Select(h =>
            new ArchiveHit(Str(h, "kind"), Str(h, "unit"), Str(h, "recordId"), h.GetProperty("at").GetDateTimeOffset(), Str(h, "snippet")))];
    }

    private static string UnitPath(string kind, string id) => $"archive/units/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(id)}";

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? string.Empty;

    private static string[] Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => x.GetString() ?? string.Empty)];

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path);
        if (_credential() is { Length: > 0 } token)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string code = "error";
            try
            {
                using var err = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                code = err.RootElement.TryGetProperty("error", out var c) ? c.GetString() ?? code : code;
            }
            catch (JsonException)
            {
                // A non-JSON refusal keeps the generic code.
            }

            throw new ArchiveRequestException((int)response.StatusCode, code);
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
