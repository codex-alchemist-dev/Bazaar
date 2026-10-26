using System.Net;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>Shared helpers for the FreeLLMAPI tests. Every key here is a placeholder, never a credential.</summary>
internal static class FreeLlmApiTestRig
{
    public const string TestKey = "TEST-KEY-NOT-A-SECRET";

    public const string BaseUrl = "http://localhost:3001/v1";

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gateway", "FreeLlmApi", "Fixtures", name));

    /// <summary>Reads a <c>.headers</c> sidecar (one <c>Name: value</c> per line) into a response with those headers set.</summary>
    public static HttpResponseMessage ResponseWithHeaders(string headersFile, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status);
        foreach (var line in Fixture(headersFile).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            response.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return response;
    }

    /// <summary>Wraps a canned reply so the response also carries the routing headers from a sidecar file.</summary>
    public static Func<CancellationToken, Task<HttpResponseMessage>> WithHeaders(
        Func<CancellationToken, Task<HttpResponseMessage>> reply,
        string headersFile) => async ct =>
    {
        var response = await reply(ct);
        foreach (var header in ResponseWithHeaders(headersFile).Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    };

    /// <summary>An HTTP client over the stub handler, with the timeout the probes expect to be set.</summary>
    public static HttpClient Client(StubHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        return http;
    }
}
