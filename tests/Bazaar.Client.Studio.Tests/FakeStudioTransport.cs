using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;

namespace Bazaar.Client.Studio.Tests;

/// <summary>An in-memory Studio MCP transport that replays a recorded fixture and records the calls it receives.</summary>
internal sealed class FakeStudioTransport : IStudioMcpTransport
{
    private readonly Dictionary<string, JsonElement> _responses = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public List<(string Method, string? Tool, JsonElement Params)> Calls { get; } = [];

    public bool Open { get; set; } = true;

    public bool StartOpens { get; set; }

    public Func<string, Task>? Before { get; set; }

    public Func<string, Exception?>? Fail { get; set; }

    public bool IsStudioOpen => Open;

    public event EventHandler? IsStudioOpenChanged
    {
        add { }
        remove { }
    }

    public static FakeStudioTransport FromFixture(string name = "studio-session.json")
    {
        var fake = new FakeStudioTransport();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
        foreach (var entry in doc.RootElement.GetProperty("responses").EnumerateArray())
        {
            fake._responses[entry.GetProperty("tool").GetString()!] = entry.GetProperty("result").Clone();
        }

        return fake;
    }

    public void Set(string tool, JsonElement result) => _responses[tool] = result;

    public int CallsTo(string tool)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.Tool == tool);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (StartOpens)
        {
            Open = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Open = false;
        return Task.CompletedTask;
    }

    public async Task<JsonElement> CallAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default)
    {
        var p = parameters ?? default;
        var tool = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null;
        lock (_gate)
        {
            Calls.Add((method, tool, p));
        }

        if (tool is not null && Fail?.Invoke(tool) is { } failure)
        {
            throw failure;
        }

        if (tool is not null && Before is not null)
        {
            await Before(tool).WaitAsync(cancellationToken);
        }

        return tool is not null && _responses.TryGetValue(tool, out var result)
            ? result
            : throw new StudioTransportException("no recorded response for " + tool, -32601);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeGrantVerifier : IStudioGrantVerifier
{
    public Func<string, string, Contracts.Engines.RiskClass, CancellationToken, Task<bool>> Handler { get; set; } = (_, _, _, _) => Task.FromResult(true);

    public int Calls { get; private set; }

    public Task<bool> CoversAsync(string grantId, string tool, Contracts.Engines.RiskClass riskClass, CancellationToken cancellationToken)
    {
        Calls++;
        return Handler(grantId, tool, riskClass, cancellationToken);
    }
}
