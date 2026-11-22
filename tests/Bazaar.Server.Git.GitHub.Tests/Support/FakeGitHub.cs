using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Bazaar.Server.Git.GitHub.Tests.Support;

/// <summary>
/// A scripted HTTP handler. Responses are keyed by method and absolute path (escaped, so <c>%2F</c> stays visible). A route with
/// several queued responses plays them in order and then repeats the last one. Every request is recorded.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, List<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    private readonly ConcurrentDictionary<string, int> _cursors = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public FakeGitHub Route(HttpMethod method, string absolutePath, params HttpResponseMessage[] responses)
    {
        _routes[Key(method, absolutePath)] = responses.Select<HttpResponseMessage, Func<HttpRequestMessage, HttpResponseMessage>>(r => _ => r).ToList();
        return this;
    }

    /// <summary>A JSON route. Each play builds a fresh response, because the client disposes the one it read.</summary>
    public FakeGitHub Json(HttpMethod method, string absolutePath, string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        _routes[Key(method, absolutePath)] = [_ => Build(status, body, headers)];
        return this;
    }

    public static HttpResponseMessage Build(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            if (name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.RetryAfter = RetryConditionHeaderValue.Parse(value);
            }
            else
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    public int Count(HttpMethod method, string absolutePath) =>
        Requests.Count(r => r.Method == method && r.Path == absolutePath);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var token = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method, path, request.RequestUri, body, token));
        }

        var key = Key(request.Method, path);
        if (!_routes.TryGetValue(key, out var script))
        {
            return Build(HttpStatusCode.NotFound, """{"message":"no route in fake"}""");
        }

        var index = _cursors.AddOrUpdate(key, 0, (_, current) => current + 1);
        var step = script[Math.Min(index, script.Count - 1)];
        return step(request);
    }

    private static string Key(HttpMethod method, string path) => $"{method.Method} {path}";
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, Uri Uri, string? Body, string? Token);
