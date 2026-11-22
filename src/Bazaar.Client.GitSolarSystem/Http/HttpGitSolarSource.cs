using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.GitSolarSystem.Http;

/// <summary>
/// The solar-system feed a client head injects: it polls <c>GET /git/{provider}/repos/{owner}/{repo}/solar</c> on the Bazaar
/// server and decodes the <see cref="GitSolarFeed"/> body through <see cref="WireJsonContext"/>. Yields a snapshot when the
/// graph version increases, a frame when its version or layout sequence changes, and <see cref="GitSolarUpdate.Disconnected"/>
/// for any failed poll. A transport error, a non-2xx answer other than 404 or 409, or a malformed body backs off exponentially
/// up to <see cref="MaxBackoff"/>. A 404 or 409 waits one poll interval and does not back off. The enumeration
/// ends only when the caller's token is cancelled, except for a repository string that is not provider/owner/repo, which
/// yields one <see cref="GitSolarUpdate.Disconnected"/> and ends.
/// </summary>
public sealed class HttpGitSolarSource(
    HttpClient http,
    Func<string?> credential,
    TimeSpan? pollInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IGitSolarSource
{
    /// <summary>The longest wait between retries after failed polls.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The first retry wait after a failed poll. Each further failure doubles it, up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The wait between successful polls.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));
    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>
    /// Creates a source bound to <paramref name="endpoint"/>: its base address, and its credential on every request. The
    /// <paramref name="handler"/> is for tests; the source owns the client.
    /// </summary>
    public static HttpGitSolarSource Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpGitSolarSource(http, () => endpoint.Credential);
    }

    public async IAsyncEnumerable<GitSolarUpdate> UpdatesAsync(
        string repository,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryBuildPath(repository, out var path))
        {
            yield return new GitSolarUpdate.Disconnected();
            yield break;
        }

        long? shownVersion = null;
        long? shownFrameVersion = null;
        long shownLayoutSeq = -1;
        var failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var poll = await PollAsync(path, cancellationToken);
            if (cancellationToken.IsCancellationRequested) yield break;

            if (poll.Feed is { } feed)
            {
                failures = 0;
                if (shownVersion is not { } seen || feed.Snapshot.GraphVersion > seen)
                {
                    shownVersion = feed.Snapshot.GraphVersion;
                    yield return new GitSolarUpdate.Snapshot(feed.Snapshot);
                }

                var frame = feed.Frame;
                if (shownFrameVersion != frame.GraphVersion || frame.LayoutSeq > shownLayoutSeq)
                {
                    shownFrameVersion = frame.GraphVersion;
                    shownLayoutSeq = frame.LayoutSeq;
                    yield return new GitSolarUpdate.Frame(frame);
                }
            }
            else
            {
                yield return new GitSolarUpdate.Disconnected();
                if (poll.Transient) failures++;
            }

            var wait = poll.Feed is not null ? _pollInterval
                : poll.Transient ? Backoff(failures)
                : _pollInterval;
            try
            {
                await _delay(wait, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
        }
    }

    /// <summary>One poll. <see cref="Feed"/> is null on failure; <see cref="Transient"/> is false for 404 and 409.</summary>
    private readonly record struct Poll(GitSolarFeed? Feed, bool Transient);

    private async Task<Poll> PollAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = _credential();
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return new Poll(null, Transient: false);
            if (!response.IsSuccessStatusCode) return new Poll(null, Transient: true);

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var feed = JsonSerializer.Deserialize(payload, WireJsonContext.Default.GitSolarFeed);
            if (feed is null || feed.Snapshot is null) return new Poll(null, Transient: true);
            return new Poll(feed, Transient: true);
        }
        catch (HttpRequestException)
        {
            return new Poll(null, Transient: true);
        }
        catch (JsonException)
        {
            return new Poll(null, Transient: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation: treat it as a transient failure.
            return new Poll(null, Transient: true);
        }
    }

    private static TimeSpan Backoff(int failures)
    {
        // failures is at least 1 here. Clamp the exponent so the doubling cannot overflow.
        var exponent = Math.Min(Math.Max(failures - 1, 0), 16);
        var wait = TimeSpan.FromTicks(InitialBackoff.Ticks << exponent);
        return wait > MaxBackoff ? MaxBackoff : wait;
    }

    /// <summary>Builds <c>git/{provider}/repos/{owner}/{repo}/solar</c> from "provider/owner/repo", escaping each segment.</summary>
    private static bool TryBuildPath(string repository, out string path)
    {
        path = string.Empty;
        var parts = (repository ?? string.Empty).Split('/');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;

        path = $"git/{Uri.EscapeDataString(parts[0])}/repos/{Uri.EscapeDataString(parts[1])}/{Uri.EscapeDataString(parts[2])}/solar";
        return true;
    }
}
