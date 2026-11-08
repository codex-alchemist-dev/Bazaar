using System.Net.Http.Json;
using System.Text.Json;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Core.Pairing;

/// <summary>The device authorization started by <see cref="PairingClient.StartAsync"/>.</summary>
/// <param name="DeviceCode">The high-entropy code the device polls with. Never shown to a person.</param>
/// <param name="IntervalSeconds">The minimum wait between polls. The server may raise it with <c>slow_down</c>.</param>
public sealed record DeviceAuthorization(string DeviceCode, int IntervalSeconds);

/// <summary>The credential pair a paired device keeps (doc 32, section 5).</summary>
public sealed record PairingGrant(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>
/// Pairing ended without a grant. <see cref="Code"/> is the RFC 8628 error (<c>expired_token</c>, <c>access_denied</c>) or
/// the server's other error code.
/// </summary>
public sealed class PairingException(string code) : Exception($"Pairing did not complete: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>
/// The device side of doc 32's pairing flow (section 3, PROPOSED): starts a device authorization with the user code, then polls
/// the token endpoint until the approver answers. The server routes <c>/pair/device-authorization</c> and <c>/pair/token</c> do not
/// exist yet, so this client is tested against a fake server only. A transport failure propagates to the caller.
/// </summary>
public sealed class PairingClient(HttpClient http, Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>The RFC 8628 grant type for the token poll.</summary>
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>How much <c>slow_down</c> raises the poll interval (RFC 8628, section 3.5).</summary>
    public static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? throw new ArgumentNullException(nameof(delay));

    /// <summary>Creates a client for <paramref name="baseAddress"/>. The <paramref name="handler"/> is for tests.</summary>
    public static PairingClient Create(Uri baseAddress, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = baseAddress;
        return new PairingClient(http, Task.Delay);
    }

    /// <summary>Starts the device authorization for <paramref name="userCode"/> (the code from the approver's offer).</summary>
    public async Task<DeviceAuthorization> StartAsync(string userCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userCode);

        using var response = await _http.PostAsJsonAsync(
            "pair/device-authorization",
            new DeviceAuthorizationRequest(userCode),
            PairingJsonContext.Default.DeviceAuthorizationRequest,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync(
            PairingJsonContext.Default.DeviceAuthorizationResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new PairingException("malformed_response");
        return new DeviceAuthorization(body.DeviceCode, body.Interval);
    }

    /// <summary>
    /// Polls the token endpoint until the approver answers. <c>authorization_pending</c> waits one interval; <c>slow_down</c> raises
    /// the interval by <see cref="SlowDownStep"/> and waits; <c>expired_token</c> and <c>access_denied</c> end pairing with a
    /// <see cref="PairingException"/>. Any other error also ends pairing.
    /// </summary>
    public async Task<PairingGrant> PollAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var interval = TimeSpan.FromSeconds(authorization.IntervalSeconds);

        while (true)
        {
            using var response = await _http.PostAsJsonAsync(
                "pair/token",
                new TokenRequest(authorization.DeviceCode, DeviceCodeGrantType),
                PairingJsonContext.Default.TokenRequest,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var token = await response.Content.ReadFromJsonAsync(
                    PairingJsonContext.Default.TokenResponse, cancellationToken).ConfigureAwait(false)
                    ?? throw new PairingException("malformed_response");
                return new PairingGrant(token.AccessToken, token.RefreshToken, token.ExpiresIn);
            }

            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            switch (error)
            {
                case "authorization_pending":
                    await _delay(interval, cancellationToken).ConfigureAwait(false);
                    break;
                case "slow_down":
                    interval += SlowDownStep;
                    await _delay(interval, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new PairingException(error);
            }
        }
    }

    /// <summary>The grant as an endpoint for <see cref="ClientServer.Use"/>: the server's base address with the access token.</summary>
    public static ServerEndpoint ToEndpoint(Uri baseAddress, PairingGrant grant)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(grant);
        return new ServerEndpoint(baseAddress, grant.AccessToken);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync(
                PairingJsonContext.Default.TokenErrorResponse, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.Error) ? "malformed_response" : body.Error;
        }
        catch (JsonException)
        {
            return "malformed_response";
        }
    }
}
