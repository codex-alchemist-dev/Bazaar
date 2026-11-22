using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>An installation access token and its GitHub expiry. The token is secret, so <see cref="ToString"/> redacts it.</summary>
/// <param name="Token">The access token. Host-side only.</param>
/// <param name="ExpiresAt">The expiry GitHub reported, or the host's usable-until time when returned by the minter.</param>
public sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>Returns the expiry with the token redacted.</summary>
    public override string ToString() => $"GitHubInstallationToken(ExpiresAt={ExpiresAt:O}, Token=[redacted])";
}

/// <summary>Creates installation access tokens on the GitHub REST API.</summary>
public interface IGitHubInstallationTokenClient
{
    /// <summary>Posts to <c>/app/installations/{id}/access_tokens</c> with the narrowed repositories and permissions.</summary>
    /// <param name="installationId">The App installation.</param>
    /// <param name="appJwt">The App JWT used as the bearer credential.</param>
    /// <param name="request">The narrowed request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<GitHubInstallationToken> CreateInstallationTokenAsync(
        long installationId,
        string appJwt,
        GitHubAppTokenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGitHubInstallationTokenClient"/> over <see cref="HttpClient"/>. The caller supplies the
/// <see cref="HttpMessageHandler"/> (owned by the caller, not disposed here) and the base address (default
/// <c>https://api.github.com/</c>). Error messages carry the HTTP status only, never the response body or any token.
/// </summary>
public sealed class HttpGitHubInstallationTokenClient : IGitHubInstallationTokenClient, IDisposable
{
    /// <summary>The GitHub REST API base address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.github.com/");

    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="handler">The message handler. The caller owns it and disposes it.</param>
    /// <param name="baseAddress">An absolute base address, or null for <see cref="DefaultBaseAddress"/>.</param>
    public HttpGitHubInstallationTokenClient(HttpMessageHandler handler, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var address = baseAddress ?? DefaultBaseAddress;
        if (!address.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be absolute.", nameof(baseAddress));
        }

        // A trailing slash keeps relative paths from dropping the last segment of the base.
        var withSlash = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");

        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = withSlash,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Bazaar.Server");
    }

    /// <inheritdoc />
    public async ValueTask<GitHubInstallationToken> CreateInstallationTokenAsync(
        long installationId,
        string appJwt,
        GitHubAppTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appJwt);
        ArgumentNullException.ThrowIfNull(request);

        var body = new TokenRequestBody(
            request.Repositories.ToArray(),
            new Dictionary<string, string>(request.Permissions, StringComparer.Ordinal));
        var json = JsonSerializer.SerializeToUtf8Bytes(body, GitHubAppJson.Default.TokenRequestBody);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens")
        {
            Content = new ByteArrayContent(json),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubAppException($"GitHub refused the installation token request (HTTP {(int)response.StatusCode}).");
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize(payload, GitHubAppJson.Default.TokenResponseBody);
        if (parsed is null || string.IsNullOrEmpty(parsed.Token))
        {
            throw new GitHubAppException("GitHub's installation token response had no token.");
        }

        return new GitHubInstallationToken(parsed.Token, parsed.ExpiresAt);
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/> (not the handler).</summary>
    public void Dispose() => _http.Dispose();
}

/// <summary>Source-generated JSON contracts for the GitHub App path (JWT header and claims, token request and response).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(TokenRequestBody))]
[JsonSerializable(typeof(TokenResponseBody))]
[JsonSerializable(typeof(JwtHeader))]
[JsonSerializable(typeof(JwtClaims))]
internal sealed partial class GitHubAppJson : JsonSerializerContext
{
}

/// <summary>The JSON body of the installation token request.</summary>
/// <param name="Repositories">Bare repository names.</param>
/// <param name="Permissions">Permission name to access level.</param>
internal sealed record TokenRequestBody(
    [property: JsonPropertyName("repositories")] string[] Repositories,
    [property: JsonPropertyName("permissions")] Dictionary<string, string> Permissions);

/// <summary>The fields of the installation token response that the host reads.</summary>
/// <param name="Token">The access token.</param>
/// <param name="ExpiresAt">The expiry.</param>
internal sealed record TokenResponseBody(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>The JWT protected header.</summary>
/// <param name="Alg">The algorithm, RS256.</param>
/// <param name="Typ">The token type, JWT.</param>
internal sealed record JwtHeader(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("typ")] string Typ);

/// <summary>The JWT claims for GitHub App authentication.</summary>
/// <param name="Iss">The App id, as a string.</param>
/// <param name="Iat">Issued-at, Unix seconds.</param>
/// <param name="Exp">Expiry, Unix seconds.</param>
internal sealed record JwtClaims(
    [property: JsonPropertyName("iss")] string Iss,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("exp")] long Exp);
