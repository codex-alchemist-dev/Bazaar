using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// Host-side GitLab REST v4 transport (doc 52), shared by the forge provider and the merge executor. Every attempt leases its own
/// short-lived credential from <see cref="IGitCredentialSource"/>, sends the secret only in the <c>PRIVATE-TOKEN</c> header and
/// disposes the credential once the response is read. 429 and 5xx responses wait for <c>Retry-After</c> (capped) or back off.
/// Nothing in this class logs or throws the token.
/// </summary>
internal sealed class GitLabApiClient
{
    internal const string ProviderIdValue = "gitlab";

    private const string _tokenHeader = "PRIVATE-TOKEN";
    private const string _nextPageHeader = "X-Next-Page";
    private const int _maxPerPage = 100;

    private readonly HttpClient _http;
    private readonly IGitCredentialSource _credentials;
    private readonly GitLabOptions _options;
    private readonly TimeProvider _time;
    private readonly string _apiRoot;

    public GitLabApiClient(HttpClient http, IGitCredentialSource credentials, GitLabOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        if (!options.BaseUrl.IsAbsoluteUri || options.BaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            // The token goes in each request's header, so the base must be https.
            throw new ArgumentException("The GitLab base URL must be an absolute https URI.", nameof(options));
        }

        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries cannot be negative.");
        }

        _http = http;
        _credentials = credentials;
        _options = options;
        _time = time;
        _apiRoot = options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/api/v4/";
    }

    /// <summary>Sends one call, retrying as the policy allows. Returns the body of the first successful response.</summary>
    public async Task<GitLabResponse> SendAsync(
        GitOperation operation,
        string fullName,
        HttpMethod method,
        string relativePath,
        IReadOnlyList<(string Name, string Value)>? query,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        var url = _apiRoot + relativePath + BuildQuery(query);
        var json = body is null ? null : JsonSerializer.Serialize(body, GitLabJsonContext.Default.JsonNode);
        var repo = new RepoRef(ProviderIdValue, fullName);

        for (var attempt = 0; ; attempt++)
        {
            var outcome = await SendOnceAsync(operation, repo, method, url, json, cancellationToken);
            if (outcome.Succeeded)
            {
                return new GitLabResponse(outcome.Body, outcome.HasNextPage);
            }

            if (outcome.Status == HttpStatusCode.NotFound)
            {
                throw new ForgeNotFoundException($"GitLab {method} {relativePath} found nothing.");
            }

            if (outcome.Status == HttpStatusCode.TooManyRequests && attempt >= _options.MaxRetries)
            {
                throw new ForgeRateLimitedException(outcome.RetryAfter);
            }

            if (attempt >= _options.MaxRetries || !IsRetryable(method, outcome.Status))
            {
                throw new GitLabApiException(outcome.Status, $"GitLab {method} {relativePath} failed with HTTP {(int)outcome.Status}.");
            }

            await Task.Delay(WaitFor(attempt, outcome.RetryAfter), _time, cancellationToken);
        }
    }

    /// <summary>Parses a response body. A body that is not JSON is a typed error, and its text is not echoed.</summary>
    public static JsonDocument Parse(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new GitLabApiException(null, $"GitLab {what} response was not valid JSON.", ex);
        }
    }

    /// <summary>The REST path of a project. The full path is URL-encoded as one segment (<c>group%2Fsub%2Fproject</c>).</summary>
    public static string ProjectPath(string fullName, string rest) => $"projects/{Uri.EscapeDataString(fullName)}/{rest}";

    public static string MergeRequestPath(PrRef pr, string suffix = "") =>
        ProjectPath(pr.FullName, $"merge_requests/{pr.Number.ToString(CultureInfo.InvariantCulture)}{suffix}");

    /// <summary>A page size, clamped to GitLab's maximum.</summary>
    public static string PerPage(int requested) => Math.Clamp(requested, 1, _maxPerPage).ToString(CultureInfo.InvariantCulture);

    private async Task<Attempt> SendOnceAsync(
        GitOperation operation,
        RepoRef repo,
        HttpMethod method,
        string url,
        string? json,
        CancellationToken cancellationToken)
    {
        using var credential = await _credentials.LeaseAsync(repo, operation, cancellationToken);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(_tokenHeader, credential.Reveal());
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return new Attempt(true, response.StatusCode, text, HasNextPage(response.Headers), null);
        }

        return new Attempt(false, response.StatusCode, string.Empty, false, ReadRetryAfter(response.Headers));
    }

    private static bool HasNextPage(HttpResponseHeaders headers) =>
        headers.TryGetValues(_nextPageHeader, out var values) && values.Any(v => !string.IsNullOrWhiteSpace(v));

    private TimeSpan? ReadRetryAfter(HttpResponseHeaders headers)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        var wait = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - _time.GetUtcNow() : null);
        if (wait is not { } span)
        {
            return null;
        }

        if (span < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return span <= _options.MaxRetryAfter ? span : _options.MaxRetryAfter;
    }

    private static bool IsRetryable(HttpMethod method, HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests
        || (method == HttpMethod.Get && (int)status is >= 500 and <= 599);

    private TimeSpan WaitFor(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait)
        {
            return wait;
        }

        // Exponential backoff when the server gave no hint: 1 s, 2 s, 4 s, and so on, capped.
        var seconds = Math.Min(Math.Pow(2, attempt), _options.MaxRetryAfter.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string BuildQuery(IReadOnlyList<(string Name, string Value)>? query)
    {
        if (query is null || query.Count == 0)
        {
            return string.Empty;
        }

        return "?" + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
    }

    private readonly record struct Attempt(bool Succeeded, HttpStatusCode Status, string Body, bool HasNextPage, TimeSpan? RetryAfter);
}

/// <summary>A successful response body and whether GitLab reports another page (<c>X-Next-Page</c> is non-empty).</summary>
internal readonly record struct GitLabResponse(string Body, bool HasNextPage);
