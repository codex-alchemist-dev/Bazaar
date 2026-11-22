using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// A keyless stand-in for a model provider: an OpenAI-compatible <c>/v1/chat/completions</c> endpoint on a loopback port that
/// streams a fixed reply. The real server talks to it through its real gateway (FreeLLMAPI source), so the whole model path
/// runs; only the model is fake. Every request is kept so a test can assert what the agent sent.
/// </summary>
public sealed class StubModelServer : IAsyncDisposable
{
    public const string Model = "stub-model";
    public const string ReplyPrefix = "Stub agent reply: ";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<StubRequest> _requests = new();

    private StubModelServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The OpenAI-compatible base URL the server's gateway is pointed at.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    public IReadOnlyCollection<StubRequest> Requests => _requests.ToArray();

    private readonly ConcurrentDictionary<string, string> _scripts = new();

    /// <summary>
    /// Scripts the model for one task: when the last user message contains <paramref name="marker"/>, the reply is
    /// <paramref name="reply"/>. This is how an acceptance test plays "the model decided to fix the bug" while the server, gateway
    /// and wire stay real. Markers are unique per test, so parallel tests sharing the model do not cross.
    /// </summary>
    public void AddScript(string marker, string reply) => _scripts[marker] = reply;

    public static async Task<StubModelServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var holder = new StubModelServer[1];

        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            var lastUser = "";
            if (doc.RootElement.TryGetProperty("messages", out var messages))
            {
                foreach (var m in messages.EnumerateArray())
                {
                    if (m.TryGetProperty("role", out var role) && role.GetString() == "user" && m.TryGetProperty("content", out var c))
                    {
                        lastUser = c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString();
                    }
                }
            }

            holder[0]._requests.Enqueue(new StubRequest(http.Request.Headers.Authorization.ToString(), doc.RootElement.GetRawText(), lastUser));
            var reply = holder[0].ScriptedReply(lastUser) ?? ReplyPrefix + lastUser;

            http.Response.ContentType = "text/event-stream";
            await WriteAsync(http, new { model = Model, choices = new[] { new { delta = new { content = reply }, finish_reason = (string?)null } } });
            await WriteAsync(http, new { model = Model, choices = new[] { new { delta = new { content = (string?)null }, finish_reason = "stop" } } });
            await WriteAsync(http, new { model = Model, choices = Array.Empty<object>(), usage = new { prompt_tokens = 12, completion_tokens = 7, total_tokens = 19 } });
            await http.Response.WriteAsync("data: [DONE]\n\n");
        });

        await app.StartAsync();
        var address = app.Urls.First();
        var stub = new StubModelServer(app, new Uri(address).Port);
        holder[0] = stub;
        return stub;
    }

    private string? ScriptedReply(string lastUser) =>
        _scripts.FirstOrDefault(kv => lastUser.Contains(kv.Key, StringComparison.Ordinal)).Value;

    private static async Task WriteAsync(HttpContext http, object chunk)
    {
        var json = JsonSerializer.Serialize(chunk);
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        await http.Response.Body.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One call the agent made to the stub model.</summary>
public sealed record StubRequest(string Authorization, string Body, string LastUserMessage);
