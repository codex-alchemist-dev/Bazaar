using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>
/// Files and updates crash issues through GitHub's REST API as the system App (doc 43 section 8.4, doc 21). It writes only to the
/// configured issue repository. A deleted or inaccessible issue raises <see cref="CrashIssueNotFoundException"/>; rate limits and
/// server errors raise <see cref="HttpRequestException"/>, which the dispatcher backs off and retries.
/// </summary>
public sealed class GitHubCrashIssueFiler(HttpClient http, ICrashGitHubTokenSource tokens, CrashFilingOptions options) : ICrashIssueFiler
{
    /// <summary>The API version header sent with every call.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <inheritdoc />
    public async Task<int> CreateIssueAsync(IssueText issue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        using var response = await SendAsync(HttpMethod.Post, $"{RepoPath()}/issues", Json(w =>
        {
            w.WriteString("title", issue.Title);
            w.WriteString("body", issue.Body);
        }), ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("number").GetInt32();
    }

    /// <inheritdoc />
    public async Task CommentAsync(int issueNumber, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var _ = await SendAsync(HttpMethod.Post, $"{RepoPath()}/issues/{issueNumber}/comments", Json(w => w.WriteString("body", text)), ct, issueNumber).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReopenAsync(int issueNumber, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using (await SendAsync(HttpMethod.Patch, $"{RepoPath()}/issues/{issueNumber}", Json(w => w.WriteString("state", "open")), ct, issueNumber).ConfigureAwait(false))
        {
        }

        await CommentAsync(issueNumber, text, ct).ConfigureAwait(false);
    }

    private string RepoPath()
    {
        var parts = options.IssueRepo.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(p => p.All(c => c == '.')) || parts.Any(p => p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
        {
            throw new InvalidOperationException("The crash issue repository must be set as owner/name.");
        }

        return $"repos/{parts[0]}/{parts[1]}";
    }

    private static StringContent Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct, int? issueNumber = null)
    {
        var token = await tokens.GetTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        // GitHub rejects API requests that carry no User-Agent.
        request.Headers.UserAgent.ParseAdd("Bazaar-Crash/1");
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        if (issueNumber is { } n && status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new CrashIssueNotFoundException(n);
        }

        throw new HttpRequestException($"GitHub answered {(int)status} for {method} {path}.", null, status);
    }
}
