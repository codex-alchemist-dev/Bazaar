using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// Host-side GitHub REST client (doc 21, Components). Each call takes a token from <see cref="IGitHubTokenProvider"/>
/// and sends it only as the Authorization header. Reads are ETag-conditional and gated by <see cref="IGitHubRateState"/>.
/// Rate-limited responses wait when the wait is short and surface as <see cref="GitHubRateLimitedException"/> otherwise.
/// 5xx and transport failures back off exponentially. Nothing in this class logs or throws the token.
/// </summary>
public sealed class GitHubAdapter : IGitHubAdapter
{
    private const string _apiVersion = "2022-11-28";
    private const string _userAgent = "Bazaar-Server";
    private const int _defaultCoreLimit = 5000;

    private static readonly Action<ILogger, string, int, int, Exception?> _logRateRetry = LoggerMessage.Define<string, int, int>(
        LogLevel.Warning,
        new EventId(1, "GitHubRateRetry"),
        "GitHub {Call} was rate limited; retrying in {WaitSeconds}s (attempt {Attempt}).");

    private static readonly Action<ILogger, string, int, int, int, Exception?> _logServerRetry = LoggerMessage.Define<string, int, int, int>(
        LogLevel.Warning,
        new EventId(2, "GitHubServerRetry"),
        "GitHub {Call} returned {Status}; retrying in {DelaySeconds}s (attempt {Attempt}).");

    private static readonly Action<ILogger, long, int, int, Exception?> _logNearLimit = LoggerMessage.Define<long, int, int>(
        LogLevel.Warning,
        new EventId(3, "GitHubNearLimit"),
        "GitHub installation {InstallationId} has used {Percent}% of its core rate limit; reads defer at {DeferPercent}%.");

    private static readonly string[] _reviewEvents = ["COMMENT", "APPROVE", "REQUEST_CHANGES"];

    // Scopes name the permissions one token needs. Repository ids are not passed (see AcquireTokenAsync).
    private static readonly IReadOnlyDictionary<string, string> _readPermissions =
        new Dictionary<string, string> { ["pull_requests"] = "read" };

    private static readonly IReadOnlyDictionary<string, string> _pullWritePermissions =
        new Dictionary<string, string> { ["pull_requests"] = "write" };

    private static readonly IReadOnlyDictionary<string, string> _mergePermissions =
        new Dictionary<string, string> { ["pull_requests"] = "write", ["contents"] = "write" };

    private readonly HttpClient _http;
    private readonly IGitHubTokenProvider _tokens;
    private readonly IGitHubRateState _rateState;
    private readonly GitHubAdapterOptions _options;
    private readonly ILogger<GitHubAdapter> _logger;
    private readonly TimeProvider _time;
    private readonly Uri _baseAddress;
    private readonly GitHubResponseCache _cache;

    public GitHubAdapter(
        HttpClient http,
        IGitHubTokenProvider tokens,
        IGitHubRateState rateState,
        GitHubAdapterOptions options,
        ILogger<GitHubAdapter> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(rateState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            // The token goes in this request's header, so the base must be https.
            throw new ArgumentException("The GitHub base address must be an absolute https URI.", nameof(options));
        }

        _http = http;
        _tokens = tokens;
        _rateState = rateState;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _baseAddress = WithTrailingSlash(options.BaseAddress);
        _cache = new GitHubResponseCache(options.MaxCacheEntries);
    }

    public async Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(request.InstallationId, _pullWritePermissions, cancellationToken);
        var repoPath = RepoPath(request.Owner, request.Repo);

        // Search first so a retried open call never creates a second PR for the same head branch.
        var head = Uri.EscapeDataString($"{request.Owner}:{request.HeadBranch}");
        var search = await GetAsync(request.InstallationId, token, Endpoint($"{repoPath}/pulls?head={head}&state=open"), cancellationToken);
        using (var found = ParseJson(search, "pull request search"))
        {
            if (found.RootElement.ValueKind == JsonValueKind.Array && found.RootElement.GetArrayLength() > 0)
            {
                return ParsePullRequest(found.RootElement[0], request.InstallationId, request.Owner, request.Repo);
            }
        }

        var body = new JsonObject
        {
            ["title"] = request.Title,
            ["body"] = request.Body,
            ["head"] = request.HeadBranch,
            ["base"] = request.BaseBranch,
            ["draft"] = true,
        };
        var created = await WriteAsync(request.InstallationId, HttpMethod.Post, token, Endpoint($"{repoPath}/pulls"), body, cancellationToken);
        using var doc = ParseJson(created, "pull request create");
        return ParsePullRequest(doc.RootElement, request.InstallationId, request.Owner, request.Repo);
    }

    public async Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(headSha);
        var token = await AcquireTokenAsync(pr.InstallationId, _readPermissions, cancellationToken);
        var pullPath = $"{RepoPath(pr.Owner, pr.Repo)}/pulls/{pr.Number.ToString(CultureInfo.InvariantCulture)}";

        // The files endpoint does not say which head they belong to, so the PR is read first and compared.
        var pull = await GetAsync(pr.InstallationId, token, Endpoint(pullPath), cancellationToken);
        using (var doc = ParseJson(pull, "pull request"))
        {
            var actual = Shape(() => doc.RootElement.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty);
            if (!string.Equals(actual, headSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitHubStaleHeadException(pr.Number, headSha, actual);
            }
        }

        var files = new List<PrFile>();
        Uri? next = Endpoint($"{pullPath}/files?per_page=100");
        while (next is not null)
        {
            var page = await GetAsync(pr.InstallationId, token, next, cancellationToken);
            using var doc = ParseJson(page, "pull request files");
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException(null, null, "GitHub pull request files response was not an array.");
            }

            foreach (var file in doc.RootElement.EnumerateArray())
            {
                files.Add(Shape(() => ParseFile(file)));
            }

            next = page.NextLink;
        }

        return files;
    }

    public async Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken)
    {
        if (Array.IndexOf(_reviewEvents, review.Event) < 0)
        {
            throw new ArgumentException("A review event must be COMMENT, APPROVE or REQUEST_CHANGES.", nameof(review));
        }

        var token = await AcquireTokenAsync(pr.InstallationId, _pullWritePermissions, cancellationToken);
        var body = new JsonObject
        {
            ["commit_id"] = review.HeadSha,
            ["event"] = review.Event,
            ["body"] = review.Body,
        };
        await WriteAsync(pr.InstallationId, HttpMethod.Post, token, Endpoint($"{PullPath(pr)}/reviews"), body, cancellationToken);
    }

    public async Task SquashMergeAsync(PrRef pr, MergeRequest merge, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(pr.InstallationId, _mergePermissions, cancellationToken);

        // The message is passed through as given. This client adds no sign-off line of its own.
        var body = new JsonObject
        {
            ["merge_method"] = "squash",
            ["sha"] = merge.HeadSha,
            ["commit_title"] = merge.CommitTitle,
            ["commit_message"] = merge.CommitMessage,
        };
        var response = await WriteAsync(pr.InstallationId, HttpMethod.Put, token, Endpoint($"{PullPath(pr)}/merge"), body, cancellationToken);
        using var doc = ParseJson(response, "pull request merge");
        var merged = doc.RootElement.TryGetProperty("merged", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!merged)
        {
            throw new GitHubApiException(null, null, "GitHub did not confirm the squash merge.");
        }
    }

    /// <summary>
    /// One token per public call. Scope lists no repository ids because the adapter works from owner and repo names.
    /// Narrowing to repository ids needs the token provider or the contract to carry them (open, see the work package report).
    /// </summary>
    private async Task<string> AcquireTokenAsync(long installationId, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(new GitHubTokenScope(installationId, Array.Empty<long>(), permissions), cancellationToken);
        return token.Value;
    }

    private Task<GitHubResponse> GetAsync(long installationId, string token, Uri uri, CancellationToken cancellationToken) =>
        ExecuteAsync(new GitHubCall(installationId, HttpMethod.Get, uri, token, null, IsRead: true), cancellationToken);

    private async Task<GitHubResponse> WriteAsync(long installationId, HttpMethod method, string token, Uri uri, JsonNode body, CancellationToken cancellationToken) =>
        await ExecuteAsync(new GitHubCall(installationId, method, uri, token, body, IsRead: false), cancellationToken);

    private async Task<GitHubResponse> ExecuteAsync(GitHubCall call, CancellationToken cancellationToken)
    {
        if (call.IsRead)
        {
            await EnsureReadAllowedAsync(call.InstallationId, cancellationToken);
        }

        for (var attempt = 0; ; attempt++)
        {
            var outcome = await SendOnceAsync(call, attempt, canRetry: attempt < _options.MaxRetries, cancellationToken);
            if (outcome.Response is { } response)
            {
                return response;
            }

            if (outcome.Error is { } error)
            {
                throw error;
            }

            await Task.Delay(outcome.Delay!.Value, _time, cancellationToken);
        }
    }

    /// <summary>Defers reads locally once the core window passes the defer threshold, until it resets.</summary>
    private async Task EnsureReadAllowedAsync(long installationId, CancellationToken cancellationToken)
    {
        var snapshot = await _rateState.GetAsync(installationId, cancellationToken);
        if (snapshot is null || _time.GetUtcNow() >= snapshot.ResetAt || snapshot.UsageRatio < _options.DeferUsageRatio)
        {
            return;
        }

        throw new GitHubRateLimitedException(
            installationId,
            snapshot.ResetAt,
            null,
            $"GitHub reads for installation {installationId} are deferred until {snapshot.ResetAt:O} " +
            $"(core rate limit {Percent(snapshot.UsageRatio)}% used).");
    }

    private async Task<AttemptOutcome> SendOnceAsync(GitHubCall call, int attempt, bool canRetry, CancellationToken cancellationToken)
    {
        var cacheKey = $"{call.InstallationId} {call.Address.AbsoluteUri}";
        CacheEntry? cached = null;
        if (call.IsRead)
        {
            _cache.TryGet(cacheKey, out cached);
        }

        using var request = new HttpRequestMessage(call.Method, call.Address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", _apiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(_userAgent, "1.0"));
        if (cached is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        if (call.Body is not null)
        {
            request.Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            if (canRetry)
            {
                return AttemptOutcome.RetryAfter(BackoffFor(attempt));
            }

            return AttemptOutcome.Fail(new GitHubApiException(null, null, $"GitHub {Describe(call)} did not complete.", ex));
        }

        using (response)
        {
            var now = _time.GetUtcNow();
            var rate = ReadRateHeaders(response.Headers, now);
            await StoreRateStateAsync(call.InstallationId, rate, now, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                return AttemptOutcome.Ok(cached.Response);
            }

            var text = response.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : string.Empty;

            if (response.IsSuccessStatusCode)
            {
                var next = ReadNextLink(response.Headers);
                var result = new GitHubResponse(text, next);
                if (call.IsRead && response.Headers.ETag is { } etag)
                {
                    _cache.Set(cacheKey, new CacheEntry(etag.ToString(), result));
                }

                return AttemptOutcome.Ok(result);
            }

            var status = response.StatusCode;
            if (status == HttpStatusCode.TooManyRequests
                || (status == HttpStatusCode.Forbidden && (rate.RetryAfter is not null || rate.Remaining == 0)))
            {
                var (wait, resetAt) = RateLimitWait(rate, now);
                if (canRetry && wait <= _options.MaxInlineRateLimitDelay)
                {
                    _logRateRetry(_logger, Describe(call), (int)wait.TotalSeconds, attempt + 1, null);
                    return AttemptOutcome.RetryAfter(wait);
                }

                return AttemptOutcome.Fail(new GitHubRateLimitedException(
                    call.InstallationId,
                    resetAt,
                    status,
                    ErrorMessage(call, status, text)));
            }

            if ((int)status >= 500 && canRetry)
            {
                var delay = BackoffFor(attempt);
                _logServerRetry(_logger, Describe(call), (int)status, (int)delay.TotalSeconds, attempt + 1, null);
                return AttemptOutcome.RetryAfter(delay);
            }

            return AttemptOutcome.Fail(new GitHubApiException(status, GitHubMessage(text, call.Token), ErrorMessage(call, status, text)));
        }
    }

    private async Task StoreRateStateAsync(long installationId, RateHeaders rate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Without a remaining count and a reset time there is no window to record.
        if (rate.Remaining is not { } remaining || rate.Reset is not { } reset)
        {
            return;
        }

        var limit = rate.Limit ?? _defaultCoreLimit;
        var snapshot = new GitHubRateSnapshot(limit, Math.Clamp(remaining, 0, limit), reset);
        await _rateState.SetAsync(installationId, snapshot, cancellationToken);

        if (now < reset && snapshot.UsageRatio >= _options.WarnUsageRatio)
        {
            _logNearLimit(_logger, installationId, Percent(snapshot.UsageRatio), Percent(_options.DeferUsageRatio), null);
        }
    }

    private (TimeSpan Wait, DateTimeOffset ResetAt) RateLimitWait(RateHeaders rate, DateTimeOffset now)
    {
        if (rate.RetryAfter is { } retryAfter)
        {
            return (retryAfter, now + retryAfter);
        }

        if (rate.Reset is { } reset)
        {
            var wait = reset - now;
            return (wait < TimeSpan.Zero ? TimeSpan.Zero : wait, reset);
        }

        return (_options.RateLimitFallbackDelay, now + _options.RateLimitFallbackDelay);
    }

    private TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromTicks((long)(_options.RetryBaseDelay.Ticks * Math.Pow(2, attempt)));

    /// <summary>Reads the Link header and returns its <c>rel="next"</c> target, refusing any target off the configured host.</summary>
    private Uri? ReadNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            foreach (var part in value.Split(','))
            {
                var pieces = part.Split(';', StringSplitOptions.TrimEntries);
                if (pieces.Length < 2 || !pieces.Skip(1).Any(p => string.Equals(p, "rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var target = pieces[0];
                if (target.Length < 2 || target[0] != '<' || target[^1] != '>'
                    || !Uri.TryCreate(target[1..^1], UriKind.Absolute, out var next)
                    || !SameOrigin(next))
                {
                    throw new GitHubApiException(null, null, "GitHub pagination link points outside the configured GitHub host.");
                }

                return next;
            }
        }

        return null;
    }

    private bool SameOrigin(Uri uri) =>
        uri.Scheme == _baseAddress.Scheme
        && uri.Port == _baseAddress.Port
        && string.Equals(uri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase);

    private static RateHeaders ReadRateHeaders(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var limit = ReadInt(headers, "x-ratelimit-limit");
        var remaining = ReadInt(headers, "x-ratelimit-remaining");
        var resetSeconds = ReadInt64(headers, "x-ratelimit-reset");
        var reset = resetSeconds is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
        return new RateHeaders(limit, remaining, reset, ReadRetryAfter(headers, now));
    }

    private static int? ReadInt(HttpResponseHeaders headers, string name) =>
        ReadRaw(headers, name) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ReadInt64(HttpResponseHeaders headers, string name) =>
        ReadRaw(headers, name) is { } raw && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (ReadRaw(headers, "retry-after") is not { } raw)
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static string? ReadRaw(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string ErrorMessage(GitHubCall call, HttpStatusCode status, string text)
    {
        var detail = GitHubMessage(text, call.Token);
        var suffix = detail is null ? string.Empty : $": {detail}";
        return $"GitHub {Describe(call)} failed with HTTP {(int)status}{suffix}";
    }

    /// <summary>Returns the <c>message</c> field of an error body with the token value removed, or null.</summary>
    private static string? GitHubMessage(string text, string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var value = message.GetString();
                return string.IsNullOrEmpty(value) ? null : Redact(value, token);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the status code alone describes the failure.
        }

        return null;
    }

    private static string Redact(string text, string token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "[redacted]", StringComparison.Ordinal);

    private static string Describe(GitHubCall call) => $"{call.Method} {call.Address.AbsolutePath}";

    private static int Percent(double ratio) => (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);

    private static JsonDocument ParseJson(GitHubResponse response, string description)
    {
        try
        {
            return JsonDocument.Parse(response.Body);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(null, null, $"GitHub {description} returned a body that is not JSON.", ex);
        }
    }

    private static T Shape<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new GitHubApiException(null, null, "GitHub response did not have the expected shape.", ex);
        }
    }

    private static PullRequestRef ParsePullRequest(JsonElement pr, long installationId, string owner, string repo) =>
        Shape(() => new PullRequestRef(
            new PrRef(installationId, owner, repo, pr.GetProperty("number").GetInt32()),
            pr.GetProperty("node_id").GetString() ?? string.Empty,
            pr.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty,
            pr.GetProperty("draft").GetBoolean()));

    private static PrFile ParseFile(JsonElement file) =>
        new(
            file.GetProperty("filename").GetString() ?? string.Empty,
            file.GetProperty("status").GetString() ?? string.Empty,
            file.GetProperty("additions").GetInt32(),
            file.GetProperty("deletions").GetInt32(),
            file.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.String ? patch.GetString() : null);

    private Uri Endpoint(string relative) => new(_baseAddress, relative);

    private static string RepoPath(string owner, string repo) =>
        $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    private static string PullPath(PrRef pr) =>
        $"{RepoPath(pr.Owner, pr.Repo)}/pulls/{pr.Number.ToString(CultureInfo.InvariantCulture)}";

    private static Uri WithTrailingSlash(Uri uri)
    {
        var text = uri.AbsoluteUri;
        return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
    }

    private sealed record GitHubCall(long InstallationId, HttpMethod Method, Uri Address, string Token, JsonNode? Body, bool IsRead);

    private sealed record RateHeaders(int? Limit, int? Remaining, DateTimeOffset? Reset, TimeSpan? RetryAfter);

    /// <summary>Exactly one of: a response, a terminal error, or a delay before the next attempt.</summary>
    private sealed record AttemptOutcome(GitHubResponse? Response, Exception? Error, TimeSpan? Delay)
    {
        public static AttemptOutcome Ok(GitHubResponse response) => new(response, null, null);

        public static AttemptOutcome Fail(Exception error) => new(null, error, null);

        public static AttemptOutcome RetryAfter(TimeSpan delay) => new(null, null, delay);
    }
}
