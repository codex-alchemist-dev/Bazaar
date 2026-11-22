using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Community.GitHub;

/// <summary>
/// <see cref="ICommunityGitHubApi"/> over the GitHub REST API (api.github.com), authenticated as the App installation through
/// <see cref="ICommunityGitHubTokenSource"/>. JSON is written with <see cref="Utf8JsonWriter"/> and read with <see cref="JsonDocument"/>
/// so the module stays trimming and AOT safe. Rate limits (429, or 403 with a rate-limit signal) and 5xx are retryable and carry the
/// wait in <c>Exception.Data["RetryAfterSeconds"]</c>; other failures are not. No exception message ever contains a header or token.
/// </summary>
public sealed class HttpCommunityGitHubApi(HttpClient http, ICommunityGitHubTokenSource tokens) : ICommunityGitHubApi
{
    /// <summary>Key in <see cref="Exception.Data"/> holding the suggested wait, in whole seconds, for a rate-limited call.</summary>
    public const string RetryAfterDataKey = "RetryAfterSeconds";

    public static Uri DefaultBaseAddress { get; } = new("https://api.github.com/");

    private const int _commentPageSize = 100;
    private const int _maxCommentPages = 10;

    public async Task<CommunityGitHubItem> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken cancellationToken)
    {
        var payload = Json(w =>
        {
            w.WriteString("title", title);
            w.WriteString("body", body);
        });
        using var doc = await SendAsync(HttpMethod.Post, $"repos/{owner}/{repo}/issues", owner, repo, payload, cancellationToken).ConfigureAwait(false);
        return ReadItem(doc.RootElement);
    }

    public async Task<CommunityGitHubItem> CreateCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken)
    {
        var payload = Json(w => w.WriteString("body", body));
        using var doc = await SendAsync(
            HttpMethod.Post, $"repos/{owner}/{repo}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}/comments", owner, repo, payload, cancellationToken)
            .ConfigureAwait(false);
        return ReadItem(doc.RootElement);
    }

    public async Task<CommunityGitHubItem?> FindByMarkerAsync(
        string owner, string repo, int? issueNumber, string marker, CancellationToken cancellationToken)
    {
        if (issueNumber is null)
        {
            // Search matches on tokens, not exact text, so each hit's body is checked for the full marker. The search index lags a
            // little behind writes; a retry a few seconds later still finds the item, and the marker check keeps false hits out.
            var token = marker.Replace("<!--", string.Empty, StringComparison.Ordinal).Replace("-->", string.Empty, StringComparison.Ordinal).Trim();
            var query = Uri.EscapeDataString($"\"{token}\" repo:{owner}/{repo} in:body");
            using var search = await SendAsync(HttpMethod.Get, $"search/issues?q={query}&per_page=10", owner, repo, null, cancellationToken).ConfigureAwait(false);
            if (search.RootElement.TryGetProperty("items", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in hits.EnumerateArray())
                {
                    if (BodyContains(hit, marker))
                    {
                        return ReadItem(hit);
                    }
                }
            }

            return null;
        }

        for (var page = 1; page <= _maxCommentPages; page++)
        {
            var path = $"repos/{owner}/{repo}/issues/{issueNumber.Value.ToString(CultureInfo.InvariantCulture)}/comments?per_page={_commentPageSize}&page={page}";
            using var comments = await SendAsync(HttpMethod.Get, path, owner, repo, null, cancellationToken).ConfigureAwait(false);
            if (comments.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var count = 0;
            foreach (var comment in comments.RootElement.EnumerateArray())
            {
                count++;
                if (BodyContains(comment, marker))
                {
                    return ReadItem(comment);
                }
            }

            if (count < _commentPageSize)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string relativePath, string owner, string repo, byte[]? payload, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await tokens.GetInstallationTokenAsync(owner, repo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CommunityPostException("Could not obtain a GitHub installation token.", retryable: true, ex);
        }

        using var request = new HttpRequestMessage(method, new Uri(DefaultBaseAddress, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Bazaar-Community", "0.1"));
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Transport failure. The message is replaced so nothing from the request (URL, headers) is repeated.
            throw new CommunityPostException("GitHub could not be reached.", retryable: true, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommunityPostException("The GitHub request timed out.", retryable: true, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response, text);
            }

            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new CommunityPostException("GitHub returned an unreadable response.", retryable: false, ex);
            }
        }
    }

    private static CommunityPostException Failure(HttpResponseMessage response, string text)
    {
        var status = (int)response.StatusCode;
        var apiMessage = ApiMessage(text);
        var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimitSignal(response, apiMessage));

        if (rateLimited)
        {
            var wait = RetryAfter(response);
            var ex = new CommunityPostException(
                $"GitHub rate limit hit (HTTP {status.ToString(CultureInfo.InvariantCulture)})"
                + (wait is { } w ? $"; retry after {((int)w.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s." : "."),
                retryable: true);
            if (wait is { } seconds)
            {
                ex.Data[RetryAfterDataKey] = (int)seconds.TotalSeconds;
            }

            return ex;
        }

        var retryable = status >= 500;
        var detail = apiMessage is null ? string.Empty : $": {apiMessage}";
        return new CommunityPostException($"GitHub refused the request (HTTP {status.ToString(CultureInfo.InvariantCulture)}){detail}", retryable);
    }

    private static bool IsRateLimitSignal(HttpResponseMessage response, string? apiMessage) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        || (apiMessage?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false);

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>The <c>message</c> field of a GitHub error body, shortened, or null.</summary>
    private static string? ApiMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var message = m.GetString();
                return message is { Length: > 200 } ? message[..200] : message;
            }
        }
        catch (JsonException)
        {
            // Not JSON (for example an HTML error page): no detail.
        }

        return null;
    }

    private static bool BodyContains(JsonElement element, string marker) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("body", out var body)
        && body.ValueKind == JsonValueKind.String
        && (body.GetString()?.Contains(marker, StringComparison.Ordinal) ?? false);

    private static CommunityGitHubItem ReadItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
            || !element.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
        {
            throw new CommunityPostException("GitHub returned an item without an id or URL.", retryable: false);
        }

        return new CommunityGitHubItem(id.GetInt64().ToString(CultureInfo.InvariantCulture), uri);
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
