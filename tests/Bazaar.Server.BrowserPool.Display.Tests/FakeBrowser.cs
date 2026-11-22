using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

/// <summary>One command the fake browser received.</summary>
internal sealed record ReceivedCommand(long Id, string Method, string? SessionId, string ParamsJson, string Raw)
{
    public JsonElement Params => JsonElement.Parse(ParamsJson);
}

/// <summary>What the fake sends back for a command: a result object, an error code, or nothing at all.</summary>
internal sealed record FakeReply(string? ResultJson, int? ErrorCode = null, bool Silent = false)
{
    public static FakeReply Result(string json) => new(json);

    public static FakeReply Error(int code) => new(null, code);

    public static FakeReply None { get; } = new(null, null, true);
}

/// <summary>
/// An in-memory browser that speaks the NUL-framed DevTools protocol over two <see cref="Pipe"/>s, so the driver sees real streams
/// with back-pressure. It records every command, and a handler decides the reply.
/// </summary>
internal sealed class FakeBrowser : IAsyncDisposable
{
    /// <summary>A valid 1x1 PNG.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly Pipe _toBrowser = new();
    private readonly Pipe _fromBrowser = new();
    private readonly DevToolsMessageWriter _writer;
    private readonly DevToolsMessageReader _reader;
    private readonly Lock _lock = new();
    private readonly List<ReceivedCommand> _received = [];
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();

    public FakeBrowser()
    {
        Pipe = new PoolDevToolsPipe(_toBrowser.Writer.AsStream(), _fromBrowser.Reader.AsStream());
        _writer = new DevToolsMessageWriter(_fromBrowser.Writer.AsStream(), 64 * 1024 * 1024);
        _reader = new DevToolsMessageReader(_toBrowser.Reader.AsStream());
        Handler = DefaultHandler;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The streams the driver gets from the "pool".</summary>
    public PoolDevToolsPipe Pipe { get; }

    /// <summary>Targets the fake lists, as <c>targetInfos</c> JSON objects.</summary>
    public List<string> Targets { get; } = ["{\"targetId\":\"PAGE-1\",\"type\":\"page\",\"title\":\"\",\"url\":\"about:blank\",\"attached\":false}"];

    /// <summary>The CDP session id the fake hands out; changes when <see cref="NewSession"/> is called.</summary>
    public string CdpSession { get; private set; } = "SESSION-1";

    /// <summary>The PNG the fake returns for a screenshot.</summary>
    public byte[] Png { get; set; } = TinyPng;

    /// <summary>Overrides the screenshot's <c>data</c> text when set.</summary>
    public string? ScreenshotData { get; set; }

    /// <summary>Decides the reply to a command. May be async to delay or reorder replies.</summary>
    public Func<ReceivedCommand, Task<FakeReply>> Handler { get; set; }

    public IReadOnlyList<ReceivedCommand> Received
    {
        get
        {
            lock (_lock)
            {
                return [.. _received];
            }
        }
    }

    public IReadOnlyList<ReceivedCommand> Input => [.. Received.Where(c => c.Method.StartsWith("Input.", StringComparison.Ordinal))];

    /// <summary>After this call the old CDP session id is "gone" and a new one is handed out.</summary>
    public void NewSession() => CdpSession = "SESSION-" + Guid.NewGuid().ToString("N")[..6];

    public async Task SendEventAsync(string json) => await _writer.WriteAsync(Encoding.UTF8.GetBytes(json), CancellationToken.None);

    public async Task SendRawAsync(byte[] bytes)
    {
        await _fromBrowser.Writer.WriteAsync(bytes);
    }

    /// <summary>Ends the browser's output, as when the browser process exits.</summary>
    public void Close() => _fromBrowser.Writer.Complete();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _toBrowser.Writer.Complete();
        _fromBrowser.Writer.Complete();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }

        _writer.Dispose();
    }

    private Task<FakeReply> DefaultHandler(ReceivedCommand c)
    {
        switch (c.Method)
        {
            case "Target.getTargets":
                return Task.FromResult(FakeReply.Result("{\"targetInfos\":[" + string.Join(',', Targets) + "]}"));
            case "Target.attachToTarget":
                return Task.FromResult(FakeReply.Result("{\"sessionId\":\"" + CdpSession + "\"}"));
            case "Page.captureScreenshot":
                if (c.SessionId != CdpSession)
                {
                    return Task.FromResult(FakeReply.Error(-32001));
                }

                return Task.FromResult(FakeReply.Result("{\"data\":\"" + (ScreenshotData ?? Convert.ToBase64String(Png)) + "\"}"));
            default:
                return Task.FromResult(c.SessionId == CdpSession ? FakeReply.Result("{}") : FakeReply.Error(-32001));
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await _reader.ReadAsync(_stop.Token);
                if (message is null)
                {
                    return;
                }

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                var command = new ReceivedCommand(
                    root.GetProperty("id").GetInt64(),
                    root.GetProperty("method").GetString()!,
                    root.TryGetProperty("sessionId", out var s) ? s.GetString() : null,
                    root.TryGetProperty("params", out var p) ? p.GetRawText() : "{}",
                    Encoding.UTF8.GetString(message));
                lock (_lock)
                {
                    _received.Add(command);
                }

                // Reply on its own task so a slow reply does not block the next command (replies may come out of order).
                _ = Task.Run(() => ReplyAsync(command));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task ReplyAsync(ReceivedCommand command)
    {
        try
        {
            var reply = await Handler(command);
            if (reply.Silent)
            {
                return;
            }

            var sessionPart = command.SessionId is null ? string.Empty : ",\"sessionId\":\"" + command.SessionId + "\"";
            var json = reply.ErrorCode is { } code
                ? $"{{\"id\":{command.Id},\"error\":{{\"code\":{code},\"message\":\"browser said: SECRET-PAGE-TEXT\"}}{sessionPart}}}"
                : $"{{\"id\":{command.Id},\"result\":{reply.ResultJson}{sessionPart}}}";
            await _writer.WriteAsync(Encoding.UTF8.GetBytes(json), CancellationToken.None);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}

/// <summary>A pool host's pipe table.</summary>
internal sealed class FakePipes : IPoolDevToolsPipes
{
    private readonly Dictionary<string, PoolDevToolsPipe> _pipes = [];

    public void Add(string sessionId, PoolDevToolsPipe pipe) => _pipes[sessionId] = pipe;

    public void Remove(string sessionId) => _pipes.Remove(sessionId);

    public bool TryGetPipe(string sessionId, [NotNullWhen(true)] out PoolDevToolsPipe? pipe) => _pipes.TryGetValue(sessionId, out pipe);
}
