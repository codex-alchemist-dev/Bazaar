using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;

namespace Bazaar.Client.Studio.Tests;

public sealed class StdioMcpTransportTests
{
    private static readonly UTF8Encoding _utf8 = new(false);

    /// <summary>A scripted MCP server on an anonymous pipe pair: runs a handler per received line.</summary>
    private sealed class PipeServer : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _serverIn;
        private readonly AnonymousPipeClientStream _serverOut;
        private readonly StreamWriter _out;

        public PipeServer(Func<string, PipeServer, Task> handler)
        {
            _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
            _serverOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
            _out = new StreamWriter(_serverOut, _utf8) { AutoFlush = true, NewLine = "\n" };
            Transport = new StdioMcpTransport(
                new StreamReader(_fromServer, _utf8),
                new StreamWriter(_toServer, _utf8) { AutoFlush = true, NewLine = "\n" });
            Loop = Task.Run(async () =>
            {
                using var reader = new StreamReader(_serverIn, _utf8);
                while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
                {
                    await handler(line, this);
                }
            });
        }

        public StdioMcpTransport Transport { get; }

        public Task Loop { get; }

        public Task SendAsync(string line) => _out.WriteLineAsync(line.AsMemory(), TestContext.Current.CancellationToken);

        public void CloseServerOutput() => _out.Dispose();

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            _out.Dispose();
            _serverIn.Dispose();
            _toServer.Dispose();
            _fromServer.Dispose();
        }
    }

    private static string IdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetRawText() : "";
    }

    private static string MethodOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("method").GetString()!;
    }

    private static JsonElement Params(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task Handshake(string line, PipeServer server)
    {
        if (MethodOf(line) == "initialize")
        {
            await server.SendAsync($$$$"""{"jsonrpc":"2.0","id":{{{{IdOf(line)}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
        }
    }

    [Fact]
    public async Task Start_performs_the_handshake_and_reports_open()
    {
        var methods = new List<string>();
        await using var server = new PipeServer(async (line, s) =>
        {
            lock (methods)
            {
                methods.Add(MethodOf(line));
            }

            await Handshake(line, s);
        });

        Assert.False(server.Transport.IsStudioOpen);
        await server.Transport.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(server.Transport.IsStudioOpen);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lock (methods)
        {
            Assert.Equal(["initialize", "notifications/initialized"], methods);
        }
    }

    [Fact]
    public async Task Concurrent_calls_are_correlated_by_id_even_when_answered_out_of_order()
    {
        var held = new List<(string Id, string Name)>();
        await using var server = new PipeServer(async (line, s) =>
        {
            if (MethodOf(line) != "tools/call")
            {
                await Handshake(line, s);
                return;
            }

            using var doc = JsonDocument.Parse(line);
            var name = doc.RootElement.GetProperty("params").GetProperty("name").GetString()!;
            List<(string Id, string Name)>? flush = null;
            lock (held)
            {
                held.Add((IdOf(line), name));
                if (held.Count == 2)
                {
                    flush = [.. held];
                    held.Clear();
                }
            }

            if (flush is not null)
            {
                // Answer in reverse arrival order, with noise lines in between.
                await s.SendAsync("not json: Studio banner");
                await s.SendAsync("""{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info"}}""");
                foreach (var (id, n) in Enumerable.Reverse(flush))
                {
                    await s.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"echo":"{{{n}}}"}}""");
                }
            }
        });
        await server.Transport.StartAsync(TestContext.Current.CancellationToken);

        var a = server.Transport.CallAsync("tools/call", Params("""{"name":"alpha"}"""), TestContext.Current.CancellationToken);
        var b = server.Transport.CallAsync("tools/call", Params("""{"name":"beta"}"""), TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("alpha", results[0].GetProperty("echo").GetString());
        Assert.Equal("beta", results[1].GetProperty("echo").GetString());
    }

    [Fact]
    public async Task Jsonrpc_error_response_becomes_a_transport_exception_with_its_code()
    {
        await using var server = new PipeServer(async (line, s) =>
        {
            if (MethodOf(line) == "tools/call")
            {
                await s.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{IdOf(line)}}},"error":{"code":-32602,"message":"bad params"}}""");
            }
            else
            {
                await Handshake(line, s);
            }
        });
        await server.Transport.StartAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<StudioTransportException>(
            () => server.Transport.CallAsync("tools/call", Params("""{"name":"x"}"""), TestContext.Current.CancellationToken));

        Assert.Equal(-32602, ex.RpcCode);
        Assert.Equal("bad params", ex.Message);
    }

    [Fact]
    public async Task Server_exit_fails_pending_calls_and_closes_the_transport()
    {
        await using var server = new PipeServer((line, s) =>
        {
            if (MethodOf(line) == "tools/call")
            {
                s.CloseServerOutput(); // The process "exits" without answering.
                return Task.CompletedTask;
            }

            return Handshake(line, s);
        });
        await server.Transport.StartAsync(TestContext.Current.CancellationToken);
        var raised = new TaskCompletionSource();
        server.Transport.IsStudioOpenChanged += (_, _) => raised.TrySetResult();

        var pending = server.Transport.CallAsync("tools/call", Params("""{"name":"x"}"""), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<StudioTransportException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(server.Transport.IsStudioOpen);
        await Assert.ThrowsAsync<StudioTransportException>(
            () => server.Transport.CallAsync("tools/call", Params("""{"name":"y"}"""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancelling_a_call_releases_the_caller_and_a_late_response_is_ignored()
    {
        string? lateId = null;
        await using var server = new PipeServer(async (line, s) =>
        {
            if (MethodOf(line) == "tools/call")
            {
                lateId = IdOf(line);
            }
            else
            {
                await Handshake(line, s);
            }
        });
        await server.Transport.StartAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.Transport.CallAsync("tools/call", Params("""{"name":"x"}"""), cts.Token));

        await server.SendAsync($$$"""{"jsonrpc":"2.0","id":{{{lateId}}},"result":{}}""");
        Assert.True(server.Transport.IsStudioOpen);
    }

    [Fact]
    public async Task Real_child_process_that_exits_without_answering_fails_the_start()
    {
        // The test executable itself is the child: `--help` prints non-JSON text and exits, so the handshake fails on exit.
        var self = Environment.ProcessPath!;
        await using var transport = new StdioMcpTransport(new StdioMcpServerCommand(self, ["--help"]));

        await Assert.ThrowsAsync<StudioTransportException>(() => transport.StartAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.False(transport.IsStudioOpen);
    }

    [Fact]
    public async Task Missing_executable_is_a_transport_exception()
    {
        await using var transport = new StdioMcpTransport(new StdioMcpServerCommand("bazaar-no-such-studio-mcp-binary", []));

        await Assert.ThrowsAsync<StudioTransportException>(() => transport.StartAsync(TestContext.Current.CancellationToken));
    }
}
