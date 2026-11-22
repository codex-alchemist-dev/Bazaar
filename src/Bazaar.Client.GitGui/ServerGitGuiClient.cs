using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui;

/// <summary>One answer from the server: the HTTP status and the (already size-capped) body.</summary>
public sealed record GitHttpResponse(int StatusCode, byte[] Body);

/// <summary>
/// The authenticated GET seam to the Bazaar server (doc 52, Client). The head supplies it: it owns the endpoint, the key-hash pin
/// and the bearer credential, so GitGui never sees a credential and never references a forge. An implementation must not read
/// more than <c>maxBodyBytes</c> bytes of a body; <see cref="HttpGitTransport"/> is the HttpClient-backed one.
/// </summary>
public interface IGitHttpTransport
{
    /// <summary>GETs a path relative to the server base (for example <c>git/github/repos/o/r/prs?state=open</c>).</summary>
    Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default);

    /// <summary>POSTs a JSON body to a path relative to the server base and returns the status and the capped answer body.</summary>
    Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IGitHttpTransport"/> over an <see cref="HttpClient"/> already bound to the server (for example
/// <c>ServerEndpoint.CreateHttpClient()</c>). The bearer is read per request so a re-pair takes effect at once. A body past the
/// cap is refused while streaming, so it is never buffered whole.
/// </summary>
public sealed class HttpGitTransport(HttpClient http, Func<string?> credential) : IGitHttpTransport
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    public async Task<GitHttpResponse> GetAsync(string pathAndQuery, int maxBodyBytes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(pathAndQuery, UriKind.Relative));
        return await SendAsync(request, maxBodyBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHttpResponse> PostJsonAsync(string path, byte[] jsonBody, int maxBodyBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonBody);
        using var content = new ByteArrayContent(jsonBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = content };
        return await SendAsync(request, maxBodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHttpResponse> SendAsync(HttpRequestMessage request, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var token = _credential();
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBodyBytes)
            throw new GitResponseException("The response is larger than the client accepts.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBodyBytes)
                throw new GitResponseException("The response is larger than the client accepts.");
            buffer.Write(chunk, 0, read);
        }

        return new GitHttpResponse((int)response.StatusCode, buffer.ToArray());
    }
}

/// <summary>Base of the typed failures <see cref="ServerGitGuiClient"/> raises (besides <see cref="HeadMovedException"/>). Messages never carry response text.</summary>
public abstract class GitGuiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server answered 404: an unknown provider, repository or PR.</summary>
public sealed class GitNotFoundException(string errorCode) : GitGuiException($"Not found ({errorCode}).")
{
    /// <summary>The server's short code (for example <c>unknown_provider</c>), or <c>unknown</c>.</summary>
    public string ErrorCode { get; } = errorCode;
}

/// <summary>The server refused the request with another 4xx; only its short error code is kept.</summary>
public sealed class GitRequestException(int statusCode, string errorCode) : GitGuiException($"The server refused the request ({statusCode}, {errorCode}).")
{
    public int StatusCode { get; } = statusCode;

    public string ErrorCode { get; } = errorCode;
}

/// <summary>The server answered 403 <c>merge_not_approved</c>. It gives no reason, so neither does the client.</summary>
public sealed class GitMergeNotApprovedException() : GitGuiException("The merge was not approved.");

/// <summary>The server answered 429 <c>rate_limited</c>. <see cref="RetryAfterSeconds"/> is null when the forge gave no hint.</summary>
public sealed class GitRateLimitedException(int? retryAfterSeconds) : GitGuiException("The forge is rate limiting; try again later.")
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>The server, or the forge behind it, is unavailable (5xx, 502 <c>forge_unavailable</c>, or no connection).</summary>
public sealed class GitServerUnavailableException(string message, Exception? inner = null) : GitGuiException(message, inner);

/// <summary>The answer cannot be used: malformed JSON, an unexpected status, or a body over the cap.</summary>
public sealed class GitResponseException(string message, Exception? inner = null) : GitGuiException(message, inner);

/// <summary>
/// <see cref="IGitGuiClient"/> over the server's git read routes (doc 52, Wire and routes). Talks only through an
/// <see cref="IGitHttpTransport"/>; JSON is the source-generated <see cref="GitReadContractsJsonContext"/>. Path segments are
/// validated against the same allowlist as the routes and then escaped, so no caller text can reshape a URL.
/// </summary>
public sealed partial class ServerGitGuiClient(IGitHttpTransport transport) : IGitGuiClient
{
    /// <summary>The largest response body accepted.</summary>
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    private static readonly string[] _states = ["open", "closed", "merged", "all"];

    private readonly IGitHttpTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<IReadOnlyList<GitRepoDto>> ListReposAsync(CancellationToken cancellationToken = default)
    {
        var body = await GetOkAsync("git/repos", cancellationToken).ConfigureAwait(false);
        return Parse(body, GitReadContractsJsonContext.Default.GitRepoDtoArray);
    }

    public async Task<GitPrPageDto> ListPrsAsync(string provider, string repo, string state, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var root = RepoRoot(provider, repo);
        if (Array.IndexOf(_states, state) < 0) throw new ArgumentException("State must be open, closed, merged or all.", nameof(state));
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var path = string.Create(CultureInfo.InvariantCulture, $"{root}/prs?state={state}&page={page}&pageSize={pageSize}");
        var body = await GetOkAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(body, GitReadContractsJsonContext.Default.GitPrPageDto);
    }

    public async Task<GitPrDetailDto> GetPrAsync(string provider, string repo, long number, CancellationToken cancellationToken = default)
    {
        var body = await GetOkAsync(PrRoot(provider, repo, number), cancellationToken).ConfigureAwait(false);
        return Parse(body, GitReadContractsJsonContext.Default.GitPrDetailDto);
    }

    public async Task<IReadOnlyList<GitPrFileDto>> GetFilesAsync(string provider, string repo, long number, string headSha, CancellationToken cancellationToken = default)
    {
        var root = PrRoot(provider, repo, number);
        if (headSha is null || !ShaRegex().IsMatch(headSha)) throw new ArgumentException("The head must be a 40-character hex sha.", nameof(headSha));

        var body = await GetOkAsync($"{root}/files?head={headSha}", cancellationToken).ConfigureAwait(false);
        return Parse(body, GitReadContractsJsonContext.Default.GitPrFileDtoArray);
    }

    public async Task<IReadOnlyList<GitCheckDto>> GetChecksAsync(string provider, string repo, long number, CancellationToken cancellationToken = default)
    {
        var body = await GetOkAsync($"{PrRoot(provider, repo, number)}/checks", cancellationToken).ConfigureAwait(false);
        return Parse(body, GitReadContractsJsonContext.Default.GitCheckDtoArray);
    }

    public async Task<GitMergedDto> MergeAsync(string provider, string repo, long number, GitMergeRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = $"{PrRoot(provider, repo, number)}/merge";
        var json = JsonSerializer.SerializeToUtf8Bytes(request, GitReadContractsJsonContext.Default.GitMergeRequestDto);

        GitHttpResponse response;
        try
        {
            response = await _transport.PostJsonAsync(path, json, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitServerUnavailableException("The server could not be reached.", ex);
        }

        ArgumentNullException.ThrowIfNull(response);
        var body = response.Body ?? [];
        if (body.Length > MaxBodyBytes) throw new GitResponseException("The response is larger than the client accepts.");

        switch (response.StatusCode)
        {
            case 200:
                return Parse(body, GitReadContractsJsonContext.Default.GitMergedDto);
            case 403:
                throw new GitMergeNotApprovedException();
            case 409:
                var current = TryParse(body, GitReadContractsJsonContext.Default.GitHeadMovedDto)?.CurrentHeadSha;
                if (current is not null && ShaRegex().IsMatch(current)) throw new HeadMovedException(current);
                throw new GitRequestException(409, ErrorCode(body));
            case 404:
                throw new GitNotFoundException(ErrorCode(body));
            case 429:
                throw new GitRateLimitedException(TryParse(body, GitReadContractsJsonContext.Default.GitRateLimitedDto)?.RetryAfterSeconds);
            case >= 400 and < 500:
                throw new GitRequestException(response.StatusCode, ErrorCode(body));
            case >= 500:
                throw new GitServerUnavailableException($"The server is unavailable ({response.StatusCode}).");
            default:
                throw new GitResponseException($"Unexpected status {response.StatusCode}.");
        }
    }

    private static string RepoRoot(string provider, string repo)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(repo);
        var parts = repo.Split('/');
        if (!ProviderRegex().IsMatch(provider) || parts.Length != 2 || !SegmentRegex().IsMatch(parts[0]) || !SegmentRegex().IsMatch(parts[1]))
            throw new ArgumentException("The provider or repository is not a valid name.");

        return $"git/{Uri.EscapeDataString(provider)}/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
    }

    private static string PrRoot(string provider, string repo, long number)
    {
        var root = RepoRoot(provider, repo);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{root}/prs/{number}");
    }

    private async Task<byte[]> GetOkAsync(string path, CancellationToken ct)
    {
        GitHttpResponse response;
        try
        {
            response = await _transport.GetAsync(path, MaxBodyBytes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitServerUnavailableException("The server could not be reached.", ex);
        }

        ArgumentNullException.ThrowIfNull(response);
        var body = response.Body ?? [];
        if (body.Length > MaxBodyBytes) throw new GitResponseException("The response is larger than the client accepts.");

        var status = response.StatusCode;
        if (status == 200) return body;

        if (status == 409)
        {
            var moved = TryParse(body, GitReadContractsJsonContext.Default.GitHeadMovedDto);
            var current = moved?.CurrentHeadSha;
            if (current is not null && ShaRegex().IsMatch(current))
                throw new HeadMovedException(current);
            throw new GitRequestException(status, ErrorCode(body));
        }

        if (status == 404) throw new GitNotFoundException(ErrorCode(body));
        if (status is >= 400 and < 500) throw new GitRequestException(status, ErrorCode(body));
        if (status >= 500) throw new GitServerUnavailableException($"The server is unavailable ({status}).");
        throw new GitResponseException($"Unexpected status {status}.");
    }

    private static T Parse<T>(byte[] body, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info) ?? throw new GitResponseException("The response was empty.");
        }
        catch (JsonException ex)
        {
            throw new GitResponseException("The response was not valid.", ex);
        }
    }

    private static T? TryParse<T>(byte[] body, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The server's <c>{"error":"code"}</c>, kept only when it is a short snake_case token; anything else is <c>unknown</c>.</summary>
    private static string ErrorCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String
                && e.GetString() is { } code
                && ErrorCodeRegex().IsMatch(code))
            {
                return code;
            }
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }
}
