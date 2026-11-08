using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Client.Core.Server;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// The tile source a client head injects: it calls <c>POST /constellation/tiles</c> on the Bazaar server and decodes the binary
/// tile body. Returns null for any non-200 answer and for a transport or decode failure, which the pane treats as no data. A
/// cancelled caller token still propagates.
/// </summary>
public sealed class HttpConstellationTileSource(HttpClient http, Func<string?> credential) : IConstellationTileSource
{
    /// <summary>The content type of a binary tile body (the server's <c>application/x-bazaar-tile</c>).</summary>
    public const string BinaryContentType = "application/x-bazaar-tile";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>
    /// Creates a source bound to <paramref name="endpoint"/>: its base address, and its credential on every request. The
    /// <paramref name="handler"/> is for tests; the source owns the client. A re-pair creates a new source with the new endpoint.
    /// </summary>
    public static HttpConstellationTileSource Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = endpoint.BaseAddress;
        return new HttpConstellationTileSource(http, () => endpoint.Credential);
    }

    public async Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new ConstellationTileRequestDto(
            request.ViewId,
            request.GraphVersion,
            request.Tier.ToString(),
            request.MinX,
            request.MinY,
            request.MinZ,
            request.MaxX,
            request.MaxY,
            request.MaxZ,
            request.MaxNodes);

        using var message = new HttpRequestMessage(HttpMethod.Post, "constellation/tiles")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, ConstellationTileClientJsonContext.Default.ConstellationTileRequestDto)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var token = _credential();
        if (!string.IsNullOrEmpty(token)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            if (response.Content.Headers.ContentType?.MediaType != BinaryContentType) return null;

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return TileFrameDecoder.Decode(payload);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation: report no data rather than failing the pane's loop.
            return null;
        }
    }
}

/// <summary>The request body of <c>POST /constellation/tiles</c>. Field names follow the server's snake_case JSON.</summary>
internal sealed record ConstellationTileRequestDto(
    [property: JsonPropertyName("view_id")] string ViewId,
    [property: JsonPropertyName("graph_version")] long GraphVersion,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("min_x")] float MinX,
    [property: JsonPropertyName("min_y")] float MinY,
    [property: JsonPropertyName("min_z")] float MinZ,
    [property: JsonPropertyName("max_x")] float MaxX,
    [property: JsonPropertyName("max_y")] float MaxY,
    [property: JsonPropertyName("max_z")] float MaxZ,
    [property: JsonPropertyName("max_nodes")] int MaxNodes);

/// <summary>Source-generated JSON for the tile request (no reflection at runtime).</summary>
[JsonSerializable(typeof(ConstellationTileRequestDto))]
internal sealed partial class ConstellationTileClientJsonContext : JsonSerializerContext
{
}
