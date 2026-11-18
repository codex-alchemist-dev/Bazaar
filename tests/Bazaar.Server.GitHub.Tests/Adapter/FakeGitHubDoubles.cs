using System.Net;
using System.Text;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.GitHub.Tests;

/// <summary>A scripted reply. Converted to a fresh <see cref="HttpResponseMessage"/> on every send, so one reply can be reused.</summary>
internal sealed record Reply(HttpStatusCode Status, string? Body, IReadOnlyList<(string Name, string Value)> Headers)
{
    public static Reply Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
        new(status, body, headers);

    public static Reply NotModified(string etag) =>
        new(HttpStatusCode.NotModified, null, [("ETag", etag)]);

    public HttpResponseMessage ToResponse()
    {
        var response = new HttpResponseMessage(Status)
        {
            Content = Body is null
                ? new ByteArrayContent(Array.Empty<byte>())
                : new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in Headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

/// <summary>What the adapter sent: method, full URL, and the headers and body that matter to the contract.</summary>
internal sealed record SentRequest(string Method, string Url, string? Authorization, string? IfNoneMatch, string? Body);

/// <summary>
/// Fake GitHub endpoint. Each route is "METHOD path-and-query" with a queue of replies, consumed in order.
/// A request with no matching route gets a 404 whose body names the key, so a missing script fails loudly.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Reply>> _routes = new(StringComparer.Ordinal);

    public List<SentRequest> Requests { get; } = [];

    public void Route(HttpMethod method, string pathAndQuery, params Reply[] replies)
    {
        var key = Key(method.Method, pathAndQuery);
        if (!_routes.TryGetValue(key, out var queue))
        {
            queue = new Queue<Reply>();
            _routes[key] = queue;
        }

        foreach (var reply in replies)
        {
            queue.Enqueue(reply);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(new SentRequest(
            request.Method.Method,
            url,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("If-None-Match", out var etags) ? etags.FirstOrDefault() : null,
            body));

        var key = Key(request.Method.Method, request.RequestUri.PathAndQuery);
        if (_routes.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            return queue.Dequeue().ToResponse();
        }

        return new Reply(HttpStatusCode.NotFound, $"unscripted request: {key}", []).ToResponse();
    }

    private static string Key(string method, string pathAndQuery) => $"{method} {pathAndQuery}";
}

internal sealed class StubTokenProvider(string token) : IGitHubTokenProvider
{
    public List<GitHubTokenScope> Scopes { get; } = [];

    public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken)
    {
        Scopes.Add(scope);
        return Task.FromResult(new GitHubToken(token, new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero)));
    }
}

/// <summary>Logical clock. Each delay advances <see cref="Now"/> and returns at once, so retry tests run instantly.</summary>
internal sealed class RecordingTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        Now = Now.Add(dueTime);
        callback(state);
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
