using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>
/// Sends a device batch to <c>POST /offline-sync/batch</c> (doc 56 step 3) with the device credential as a bearer token. Any
/// non-200 answer or malformed body is reported by throwing <see cref="HttpRequestException"/>, which the replay driver treats as
/// a transport failure and retries later. The client head supplies the <see cref="HttpClient"/> (base address and handler) and the credential.
/// </summary>
public sealed class HttpOfflineSyncTransport(HttpClient http, Func<string?> credential) : IOfflineSyncTransport
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <inheritdoc />
    public async Task<OfflineBatchResult> SendAsync(OfflineWriteBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var message = new HttpRequestMessage(HttpMethod.Post, OfflineSyncWire.BatchRoute)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(OfflineSyncWire.ToDto(batch), OfflineSyncWireContext.Default.OfflineBatchRequestDto)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var token = _credential();
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException("The server refused the offline sync batch.", null, response.StatusCode);
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync(stream, OfflineSyncWireContext.Default.OfflineBatchResponseDto, cancellationToken).ConfigureAwait(false);
            return dto?.Outcomes is null ? throw new HttpRequestException("The server sent an empty offline sync answer.") : OfflineSyncWire.ToResult(dto);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("The server sent a malformed offline sync answer.", ex);
        }
    }
}
