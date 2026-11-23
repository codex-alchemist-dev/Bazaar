using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Bazaar.Client.SignIn;

/// <summary>
/// <see cref="IWhoAmIClient"/> over <c>GET /auth/whoami</c> (doc 32 section 5, PROPOSED; doc 45 section 2.6). The caller builds the
/// <see cref="HttpClient"/> from the server endpoint, so the pin and plain-http rules already apply. The token goes only in the
/// <c>Authorization</c> header, and nothing here logs it.
/// </summary>
public sealed class HttpWhoAmIClient : IWhoAmIClient
{
    private readonly HttpClient _http;

    /// <param name="http">A client whose base address is the company server.</param>
    public HttpWhoAmIClient(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task<WhoAmIResult> WhoAmIAsync(ClientSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "auth/whoami");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var reply = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            switch (reply.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                    return new WhoAmIResult(WhoAmIOutcome.InvalidToken);
                case HttpStatusCode.Forbidden:
                    return new WhoAmIResult(WhoAmIOutcome.Forbidden);
                default:
                    return new WhoAmIResult(WhoAmIOutcome.Unavailable);
            }

            var body = await reply.Content.ReadFromJsonAsync(SignInJsonContext.Default.WhoAmIReply, cancellationToken).ConfigureAwait(false);
            if (body is null || body.HumanId == Guid.Empty || body.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(body.DisplayName))
            {
                return new WhoAmIResult(WhoAmIOutcome.Unavailable);
            }

            return new WhoAmIResult(WhoAmIOutcome.Found, new SignedInPerson(body.HumanId, body.DisplayName, body.Role ?? string.Empty, body.DeviceId));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new WhoAmIResult(WhoAmIOutcome.Unavailable);
        }
    }
}
