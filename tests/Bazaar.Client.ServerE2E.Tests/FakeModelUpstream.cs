using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Client.ServerE2E.Tests;

/// <summary>
/// A stand-in for the model source the host's gateway calls (an OpenAI-compatible <c>/v1/chat/completions</c> stream, which is
/// what FreeLLMAPI speaks). Every call answers with <see cref="Reply"/>, so the test needs no key and no network.
/// </summary>
public sealed class FakeModelUpstream : IAsyncDisposable
{
    /// <summary>The text the fake model always answers with.</summary>
    public const string Reply = "Hello from the fake model.";

    private readonly WebApplication _app;
    private int _calls;

    private FakeModelUpstream(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    /// <summary>The <c>/v1</c> base URL to give the host as <c>Bazaar:Gateway:FreeLlmApi:BaseUrl</c>.</summary>
    public Uri BaseUrl { get; }

    /// <summary>How many model calls have arrived.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public static async Task<FakeModelUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeModelUpstream? self = null;
        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            Interlocked.Increment(ref self!._calls);
            http.Response.ContentType = "text/event-stream";
            var chunks = new[]
            {
                $$"""{"id":"c1","model":"fake","choices":[{"index":0,"delta":{"role":"assistant","content":"{{Reply}}"},"finish_reason":null}]}""",
                """{"id":"c1","model":"fake","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}""",
            };
            foreach (var chunk in chunks)
            {
                await http.Response.WriteAsync("data: " + chunk + "\n\n", Encoding.UTF8, http.RequestAborted);
            }

            await http.Response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8, http.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeModelUpstream(app, new Uri(address + "/v1"));
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
