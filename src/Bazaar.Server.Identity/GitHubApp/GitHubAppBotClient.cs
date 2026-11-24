using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>The GitHub App's bot account: its slug and the numeric id of its <c>[bot]</c> user.</summary>
/// <param name="Slug">The App slug (the login without the <c>[bot]</c> suffix).</param>
/// <param name="BotUserId">The numeric id of the <c>&lt;slug&gt;[bot]</c> user.</param>
public sealed record GitHubAppBot(string Slug, long BotUserId)
{
    /// <summary>The noreply address GitHub links to the bot account.</summary>
    public string NoreplyEmail => $"{BotUserId}+{Slug}[bot]@users.noreply.github.com";
}

/// <summary>Looks up the bot account of a GitHub App.</summary>
public interface IGitHubAppBotClient
{
    /// <summary>Calls <c>GET /app</c> with the App JWT, then <c>GET /users/{slug}[bot]</c>.</summary>
    ValueTask<GitHubAppBot> GetBotAsync(string appJwt, CancellationToken cancellationToken);
}

/// <summary><see cref="IGitHubAppBotClient"/> over <see cref="HttpClient"/>. The caller owns the handler. Errors carry the HTTP status only.</summary>
public sealed class HttpGitHubAppBotClient : IGitHubAppBotClient, IDisposable
{
    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="handler">The message handler, owned by the caller.</param>
    /// <param name="baseAddress">An absolute base address, or null for the GitHub REST API.</param>
    public HttpGitHubAppBotClient(HttpMessageHandler handler, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var address = baseAddress ?? HttpGitHubInstallationTokenClient.DefaultBaseAddress;
        if (!address.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be absolute.", nameof(baseAddress));
        }

        var withSlash = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
        _http = new HttpClient(handler, disposeHandler: false) { BaseAddress = withSlash, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Bazaar.Server");
    }

    /// <inheritdoc />
    public async ValueTask<GitHubAppBot> GetBotAsync(string appJwt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appJwt);

        var app = await GetAsync("app", new AuthenticationHeaderValue("Bearer", appJwt), BotJson.Default.AppBody, cancellationToken).ConfigureAwait(false);
        if (app is null || string.IsNullOrWhiteSpace(app.Slug) || app.Slug.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new GitHubAppException("GitHub's app response had no usable slug.");
        }

        var user = await GetAsync($"users/{app.Slug}%5Bbot%5D", null, BotJson.Default.UserBody, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Id <= 0)
        {
            throw new GitHubAppException("GitHub's bot user response had no id.");
        }

        return new GitHubAppBot(app.Slug, user.Id);
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/> (not the handler).</summary>
    public void Dispose() => _http.Dispose();

    private async Task<T?> GetAsync<T>(string path, AuthenticationHeaderValue? auth, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Authorization = auth;
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubAppException($"GitHub refused the bot lookup (HTTP {(int)response.StatusCode}).");
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, info);
    }
}

internal sealed record AppBody([property: JsonPropertyName("slug")] string? Slug);

internal sealed record UserBody([property: JsonPropertyName("id")] long Id);

[JsonSerializable(typeof(AppBody))]
[JsonSerializable(typeof(UserBody))]
internal sealed partial class BotJson : JsonSerializerContext
{
}
