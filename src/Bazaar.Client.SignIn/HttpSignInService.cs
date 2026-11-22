using System.Net;
using System.Net.Http.Json;

namespace Bazaar.Client.SignIn;

/// <summary>
/// Sign-in over the company server's HTTP surface. The caller builds the <see cref="HttpClient"/> from the server endpoint, so the
/// key-hash pin and the plain-http-on-loopback rule already apply (<c>ServerEndpoint.CreateHttpClient</c>); a refused endpoint fails
/// each request without touching the network, and that shows here as <see cref="ServerReachability.Unreachable"/>.
/// </summary>
public sealed class HttpSignInService : ISignInService
{
    private const string _unavailable = "The server is not available. Check that it is running and try again.";
    private const string _refused = "Sign-in was refused. Start again from the sign-in page.";

    private readonly HttpClient _http;
    private readonly ISessionStore _store;
    private readonly string _deviceClass;
    private readonly string _clientBuild;
    private readonly TimeProvider _time;

    /// <param name="http">A client whose base address is the company server.</param>
    /// <param name="store">Where the session is kept.</param>
    /// <param name="deviceClass">Device class sent to the server, such as <c>windows</c>.</param>
    /// <param name="clientBuild">Client build string sent to the server.</param>
    /// <param name="time">Clock for the token expiry. Tests pass a fixed one.</param>
    public HttpSignInService(HttpClient http, ISessionStore store, string deviceClass, string clientBuild, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceClass);
        _deviceClass = deviceClass;
        _clientBuild = clientBuild ?? string.Empty;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Uri SignInPageAddress => new(_http.BaseAddress ?? throw new InvalidOperationException("The HTTP client has no base address."), "signin?audience=native");

    /// <inheritdoc />
    public async Task<ServerReachability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var reply = await _http.GetAsync("health/ready", cancellationToken);
            return reply.IsSuccessStatusCode ? ServerReachability.Ready : ServerReachability.NotReady;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ServerReachability.Unreachable;
        }
    }

    /// <inheritdoc />
    public async Task<SignInResult> RedeemHandoffAsync(string code, CancellationToken cancellationToken = default)
    {
        var clean = HandoffCode.TryRead(code);
        if (clean is null) return new SignInResult(SignInOutcome.InvalidCode, "That does not look like a sign-in code.");

        var body = new HandoffRequest(clean, _deviceClass, _clientBuild);
        return await ExchangeAsync("auth/handoff", body, SignInJsonContext.Default.HandoffRequest, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SignInResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _store.LoadAsync(cancellationToken);
        if (saved is null) return new SignInResult(SignInOutcome.Refused, "This device is signed out.");

        var body = new RefreshRequest(saved.RefreshToken, _deviceClass, _clientBuild);
        var result = await ExchangeAsync("auth/refresh", body, SignInJsonContext.Default.RefreshRequest, cancellationToken);
        if (result.Outcome == SignInOutcome.Refused) await _store.ClearAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public Task<ClientSession?> CurrentSessionAsync(CancellationToken cancellationToken = default) => _store.LoadAsync(cancellationToken);

    /// <inheritdoc />
    public Task SignOutAsync(CancellationToken cancellationToken = default) => _store.ClearAsync(cancellationToken);

    private async Task<SignInResult> ExchangeAsync<TBody>(
        string route,
        TBody body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TBody> typeInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reply = await _http.PostAsJsonAsync(route, body, typeInfo, cancellationToken);
            if (reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                return new SignInResult(SignInOutcome.Refused, _refused);
            if (!reply.IsSuccessStatusCode) return new SignInResult(SignInOutcome.Unavailable, _unavailable);

            var tokens = await reply.Content.ReadFromJsonAsync(SignInJsonContext.Default.TokenReply, cancellationToken);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken))
                return new SignInResult(SignInOutcome.Unavailable, _unavailable);

            var session = new ClientSession(tokens.AccessToken, tokens.RefreshToken, _time.GetUtcNow().AddSeconds(Math.Max(0, tokens.ExpiresIn)));
            await _store.SaveAsync(session, cancellationToken);
            return new SignInResult(SignInOutcome.SignedIn, "Signed in.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new SignInResult(SignInOutcome.Unavailable, _unavailable);
        }
    }
}
