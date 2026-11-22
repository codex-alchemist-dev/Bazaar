using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bazaar.Client.Walkthrough;

/// <summary>
/// The walkthrough's server calls (doc 45 section 2.7, routes PROPOSED): the facts read and the account-wide progress, both for the
/// signed-in person. The caller builds the <see cref="HttpClient"/> from the server endpoint, so the key-hash pin and the loopback
/// rule already apply. The bearer token is never logged. Failures surface as exceptions for facts (the view model keeps what it
/// knew) and as "no progress" for a read of progress, so a down server never stops the app.
/// </summary>
public sealed class HttpWalkthroughService(HttpClient http, Func<Task<string?>> bearerToken) : IWalkthroughFactsSource, IWalkthroughProgressStore
{
    public const string FactsPath = "onboarding/walkthrough/facts";
    public const string ProgressPath = "prefs/walkthrough";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<Task<string?>> _bearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken));

    public async Task<WalkthroughFacts> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(HttpMethod.Get, FactsPath, null, cancellationToken).ConfigureAwait(false);
        reply.EnsureSuccessStatusCode();
        var body = await reply.Content.ReadFromJsonAsync(WalkthroughJsonContext.Default.FactsBody, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The server sent no facts.");
        return new WalkthroughFacts(body.CompanyCreated, string.IsNullOrWhiteSpace(body.FirstAgentName) ? null : body.FirstAgentName, body.FirstTaskGiven, body.FirstAgentId, body.FirstChatId);
    }

    public async Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await SendAsync(HttpMethod.Get, ProgressPath, null, cancellationToken).ConfigureAwait(false);
        if (reply.StatusCode == HttpStatusCode.NotFound) return null;
        reply.EnsureSuccessStatusCode();
        ProgressBody? body;
        try
        {
            body = await reply.Content.ReadFromJsonAsync(WalkthroughJsonContext.Default.ProgressBody, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is null) return null;
        var current = Enum.TryParse<WalkthroughStep>(body.Current, true, out var step) && Enum.IsDefined(step) ? step : WalkthroughStep.Welcome;
        var completed = (body.Completed ?? []).Select(name => Enum.TryParse<WalkthroughStep>(name, true, out var done) && Enum.IsDefined(done) ? (WalkthroughStep?)done : null)
            .OfType<WalkthroughStep>().ToArray();
        return new WalkthroughProgress(body.Dismissed, body.Finished, current, completed);
    }

    public async Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var body = new ProgressBody(progress.Dismissed, progress.Finished, progress.Current.ToString(), [.. progress.Completed.Select(step => step.ToString())]);
        using var content = JsonContent.Create(body, WalkthroughJsonContext.Default.ProgressBody);
        using var reply = await SendAsync(HttpMethod.Put, ProgressPath, content, cancellationToken).ConfigureAwait(false);
        reply.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (await _bearerToken().ConfigureAwait(false) is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Keeps progress in two places: the account (so it follows the person to every device) and a local cache (so the app works with the
/// server down). A read prefers the account and falls back to the cache; a write goes to both and never fails because one is down.
/// </summary>
public sealed class CachedWalkthroughProgressStore(IWalkthroughProgressStore account, IWalkthroughProgressStore cache) : IWalkthroughProgressStore
{
    public async Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await account.LoadAsync(cancellationToken).ConfigureAwait(false) is { } fromAccount) return fromAccount;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The server is not reachable; the cache answers.
        }

        return await cache.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default)
    {
        await cache.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
        try
        {
            await account.SaveAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Saved locally; the next save tries the account again.
        }
    }
}
