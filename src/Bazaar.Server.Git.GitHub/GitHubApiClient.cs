using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// Host-side GitHub REST transport (doc 52), shared by the forge provider and the merge executor. Every attempt leases its own
/// short-lived credential from <see cref="IGitCredentialSource"/>, sends the secret only in the <c>Authorization</c> header and
/// disposes the credential once the response is read. Rate-limit responses and GET 5xx wait (capped) and retry. Nothing in this
/// class logs or throws the token.
/// </summary>
internal sealed class GitHubApiClient
{
    internal const string ProviderIdValue = "github";

    private const int _maxPerPage = 100;
    private const string _apiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly IGitCredentialSource _credentials;
    private readonly GitHubOptions _options;
    private readonly TimeProvider _time;
    private readonly string _apiRoot;

    public GitHubApiClient(HttpClient http, IGitCredentialSource credentials, GitHubOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        if (!options.BaseUrl.IsAbsoluteUri || options.BaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            // The token goes in each request's header, so the base must be https.
            throw new ArgumentException("The GitHub base URL must be an absolute https URI.", nameof(options));
        }

        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries cannot be negative.");
        }

        _http = http;
        _credentials = credentials;
        _options = options;
        _time = time;
        _apiRoot = options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/";
    }

    /// <summary>Sends one call, retrying as the policy allows. Returns the body of the first successful response.</summary>
    public async Task<GitHubResponse> SendAsync(
        GitOperation operation,
        string fullName,
        HttpMethod method,
        string relativePath,
        IReadOnlyList<(string Name, string Value)>? query,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        var url = _apiRoot + relativePath + BuildQuery(query);
        var json = body is null ? null : JsonSerializer.Serialize(body, GitHubJsonContext.Default.JsonNode);
        var repo = new RepoRef(ProviderIdValue, fullName);

        for (var attempt = 0; ; attempt++)
        {
            var outcome = await SendOnceAsync(operation, repo, method, url, json, cancellationToken);
            if (outcome.Succeeded)
            {
                return new GitHubResponse(outcome.Body, outcome.HasNextPage);
            }

            var message = $"GitHub {method} {relativePath} failed with HTTP {(int)outcome.Status}.";
            if (outcome.RateLimited)
            {
                // A wait beyond the cap (a primary quota reset far away) is not worth holding the caller for.
                if (attempt >= _options.MaxRetries || outcome.WaitIsBeyondCap)
                {
                    throw new GitHubApiException(outcome.Status, message, null, outcome.RetryAfter);
                }

                await Task.Delay(WaitFor(attempt, outcome.RetryAfter), _time, cancellationToken);
                continue;
            }

            if (attempt >= _options.MaxRetries || !(method == HttpMethod.Get && (int)outcome.Status is >= 500 and <= 599))
            {
                throw new GitHubApiException(outcome.Status, message);
            }

            await Task.Delay(WaitFor(attempt, null), _time, cancellationToken);
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
            throw new GitHubApiException(null, $"GitHub {what} response was not valid JSON.", ex);
        }
    }

    /// <summary>The REST path of a repository (<c>repos/owner/name/rest</c>). The full name must be exactly <c>owner/name</c>.</summary>
    public static string RepoPath(string fullName, string rest)
    {
        var parts = fullName.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A GitHub repository full name must be 'owner/name'.", nameof(fullName));
        }

        return $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/{rest}";
    }

    public static string PullPath(PrRef pr, string suffix = "") =>
        RepoPath(pr.FullName, $"pulls/{pr.Number.ToString(CultureInfo.InvariantCulture)}{suffix}");

    /// <summary>A page size, clamped to GitHub's maximum.</summary>
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
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.Reveal());
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _apiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", "Bazaar-Git");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return new Attempt(true, response.StatusCode, text, HasNextPage(response.Headers), false, null, false);
        }

        var (limited, wait, beyondCap) = ReadRateLimit(response);
        return new Attempt(false, response.StatusCode, string.Empty, false, limited, wait, beyondCap);
    }

    private static bool HasNextPage(HttpResponseHeaders headers) =>
        headers.TryGetValues("Link", out var values) && values.Any(v => v.Contains("rel=\"next\"", StringComparison.Ordinal));

    /// <summary>
    /// Whether the response is a rate limit, and how long to wait. 429 is always one. A 403 is one when it carries
    /// <c>Retry-After</c> (secondary limit) or <c>x-ratelimit-remaining: 0</c> (primary limit, reset at <c>x-ratelimit-reset</c>).
    /// </summary>
    private (bool Limited, TimeSpan? Wait, bool BeyondCap) ReadRateLimit(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? hinted = retryAfter is null
            ? null
            : retryAfter.Delta ?? (retryAfter.Date is { } date ? date - _time.GetUtcNow() : null);
        var exhausted = TryGetHeader(response, "x-ratelimit-remaining") == "0";
        var limited = response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && (hinted is not null || exhausted));
        if (!limited)
        {
            return (false, null, false);
        }

        if (hinted is { } span)
        {
            span = span < TimeSpan.Zero ? TimeSpan.Zero : span;
            return (true, span <= _options.MaxRetryAfter ? span : _options.MaxRetryAfter, false);
        }

        if (exhausted && long.TryParse(TryGetHeader(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(reset) - _time.GetUtcNow();
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            return (true, wait, wait > _options.MaxRetryAfter);
        }

        return (true, null, false);
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

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

    private readonly record struct Attempt(bool Succeeded, HttpStatusCode Status, string Body, bool HasNextPage, bool RateLimited, TimeSpan? RetryAfter, bool WaitIsBeyondCap);
}

/// <summary>A successful response body and whether GitHub reports another page (<c>Link</c> has <c>rel="next"</c>).</summary>
internal readonly record struct GitHubResponse(string Body, bool HasNextPage);
