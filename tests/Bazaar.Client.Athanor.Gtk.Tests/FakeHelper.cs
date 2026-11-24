using System.Text.Json;
using System.Threading.Channels;

namespace Bazaar.Client.Athanor.Gtk.Tests;

/// <summary>An in-process stand-in for the GTK helper: the test plays the helper's side of the line protocol.</summary>
internal sealed class FakeHelper : IEditorHelperConnection
{
    private readonly Channel<string> _toProxy = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _fromProxy = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _editorSeq;

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public Task<int> Exited => _exited.Task;

    public string? DiagnosticTail => null;

    /// <summary>When set, every <c>helper.ping</c> from the proxy is answered with a pong.</summary>
    public bool AutoPong { get; set; }

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_exited.Task.IsCompleted)
        {
            throw new IOException("The helper is gone.");
        }

        _fromProxy.Writer.TryWrite(line);
        if (AutoPong && line.Contains("\"helper.ping\"", StringComparison.Ordinal))
        {
            Send($"{{\"type\":\"helper.pong\",\"id\":{JsonElement.Parse(line).GetProperty("id").GetInt64()}}}");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ChannelReader<string> reader = _toProxy.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TryRead(out string? line))
            {
                return line;
            }
        }

        return null;
    }

    public void Kill()
    {
        Killed = true;
        End(-9);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        End(-1);
        return ValueTask.CompletedTask;
    }

    /// <summary>The helper process ends by itself.</summary>
    public void Exit(int code) => End(code);

    /// <summary>Sends one raw line to the proxy.</summary>
    public void Send(string line) => _toProxy.Writer.TryWrite(line);

    public void SendHello(bool sandbox = true, int protocol = 1) =>
        Send($"{{\"type\":\"helper.hello\",\"protocol\":{protocol},\"webkit\":\"2.52.6\",\"sandbox\":{(sandbox ? "true" : "false")},\"pid\":42}}");

    /// <summary>An editor message: the fields are written after <c>type</c> and a rising <c>seq</c>.</summary>
    public void SendEditor(string type, string fieldsJson = "")
    {
        long seq = Interlocked.Increment(ref _editorSeq);
        Send($"{{\"type\":\"{type}\",\"seq\":{seq}{(fieldsJson.Length > 0 ? "," + fieldsJson : string.Empty)}}}");
    }

    public void SendEditorWithSeq(string type, long seq, string fieldsJson = "") =>
        Send($"{{\"type\":\"{type}\",\"seq\":{seq}{(fieldsJson.Length > 0 ? "," + fieldsJson : string.Empty)}}}");

    public void SendReady() => SendEditor("ready");

    public void SendHandshake()
    {
        SendHello();
        SendReady();
    }

    public void SendChanged(string documentId, long version, string content, bool dirty = true) =>
        SendEditor("changed", $"\"documentId\":{JsonSerializer.Serialize(documentId)},\"version\":{version},\"content\":{JsonSerializer.Serialize(content)},\"dirty\":{(dirty ? "true" : "false")}");

    /// <summary>Waits for the next line the proxy wrote.</summary>
    public async Task<string> ReadFromProxyAsync(int timeoutMilliseconds = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            return await _fromProxy.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The proxy wrote nothing in time.");
        }
    }

    /// <summary>Reads the next line and returns it parsed. Disposal is the caller's job.</summary>
    public async Task<JsonElement> ReadJsonFromProxyAsync(int timeoutMilliseconds = 5000)
    {
        string line = await ReadFromProxyAsync(timeoutMilliseconds);
        return JsonElement.Parse(line);
    }

    public bool HasPendingFromProxy => _fromProxy.Reader.TryPeek(out _);

    private void End(int code)
    {
        _toProxy.Writer.TryComplete();
        _exited.TrySetResult(code);
    }
}

/// <summary>Hands out <see cref="FakeHelper"/> instances and records them.</summary>
internal sealed class FakeLauncher : IEditorHelperLauncher
{
    private readonly object _lock = new();
    private readonly List<FakeHelper> _launched = [];
    private readonly Queue<Action<FakeHelper>> _scripts = new();

    /// <summary>When set, the launch throws this.</summary>
    public Exception? LaunchFailure { get; set; }

    /// <summary>Runs on each new helper before it is returned; the default script does the full handshake.</summary>
    public Action<FakeHelper>? DefaultScript { get; set; } = h =>
    {
        h.AutoPong = true;
        h.SendHandshake();
    };

    public int LaunchCount
    {
        get
        {
            lock (_lock)
            {
                return _launched.Count;
            }
        }
    }

    public IReadOnlyList<FakeHelper> Launched
    {
        get
        {
            lock (_lock)
            {
                return [.. _launched];
            }
        }
    }

    /// <summary>The script for the next launch only.</summary>
    public void EnqueueScript(Action<FakeHelper> script)
    {
        lock (_lock)
        {
            _scripts.Enqueue(script);
        }
    }

    public ValueTask<IEditorHelperConnection> LaunchAsync(CancellationToken cancellationToken)
    {
        if (LaunchFailure is { } failure)
        {
            lock (_lock)
            {
                _launched.Add(new FakeHelper());
            }

            throw failure;
        }

        var helper = new FakeHelper();
        Action<FakeHelper>? script;
        lock (_lock)
        {
            _launched.Add(helper);
            script = _scripts.Count > 0 ? _scripts.Dequeue() : DefaultScript;
        }

        script?.Invoke(helper);
        return ValueTask.FromResult<IEditorHelperConnection>(helper);
    }

    /// <summary>Waits until at least <paramref name="count"/> helpers were launched.</summary>
    public async Task<FakeHelper> WaitForLaunchAsync(int count, int timeoutMilliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (LaunchCount < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {LaunchCount} helpers were launched.");
            }

            await Task.Delay(10);
        }

        return Launched[count - 1];
    }
}
