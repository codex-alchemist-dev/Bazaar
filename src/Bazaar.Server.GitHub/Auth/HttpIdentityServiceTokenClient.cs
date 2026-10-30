using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.GitHub.Auth;

/// <summary>
/// Calls the Identity Service exchange endpoint over an injected <see cref="HttpClient"/>. Only the scope goes out.
/// Error bodies are read for their error code only, and response bodies are never logged.
/// </summary>
public sealed class HttpIdentityServiceTokenClient : IIdentityServiceTokenClient
{
    private readonly HttpClient _http;
    private readonly IdentityServiceGitHubTokenOptions _options;

    public HttpIdentityServiceTokenClient(HttpClient http, IdentityServiceGitHubTokenOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IdentityTokenResponse> ExchangeAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var body = new IdentityExchangeRequest
        {
            InstallationId = scope.InstallationId,
            RepositoryIds = scope.RepositoryIds.ToArray(),
            Permissions = new Dictionary<string, string>(scope.Permissions, StringComparer.Ordinal),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ExchangePath)
        {
            Content = JsonContent.Create(body, IdentityServiceJsonContext.Default.IdentityExchangeRequest),
        };

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var ok = await response.Content
                        .ReadFromJsonAsync(IdentityServiceJsonContext.Default.IdentityExchangeResponse, cancellationToken)
                        .ConfigureAwait(false);
                    return ok is null
                        ? new IdentityTokenResponse(status, null, null, "invalid_response")
                        : new IdentityTokenResponse(status, ok.Token, ok.ExpiresAt, null);
                }
                catch (JsonException)
                {
                    return new IdentityTokenResponse(status, null, null, "invalid_response");
                }
            }

            string? errorCode = null;
            try
            {
                var error = await response.Content
                    .ReadFromJsonAsync(IdentityServiceJsonContext.Default.IdentityErrorResponse, cancellationToken)
                    .ConfigureAwait(false);
                errorCode = error?.Error;
            }
            catch (JsonException)
            {
                // A non-JSON error body yields no error code; the status alone is reported.
            }

            return new IdentityTokenResponse(status, null, null, errorCode);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubTokenException(
                "GitHub installation token exchange could not reach the Identity Service.",
                innerException: ex);
        }
    }
}

// TODO(doc 14): confirm request and response shapes with the Identity Service contract.
internal sealed class IdentityExchangeRequest
{
    public long InstallationId { get; init; }

    public long[] RepositoryIds { get; init; } = [];

    public Dictionary<string, string> Permissions { get; init; } = new();
}

internal sealed class IdentityExchangeResponse
{
    public string? Token { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

internal sealed class IdentityErrorResponse
{
    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdentityExchangeRequest))]
[JsonSerializable(typeof(IdentityExchangeResponse))]
[JsonSerializable(typeof(IdentityErrorResponse))]
internal sealed partial class IdentityServiceJsonContext : JsonSerializerContext
{
}
