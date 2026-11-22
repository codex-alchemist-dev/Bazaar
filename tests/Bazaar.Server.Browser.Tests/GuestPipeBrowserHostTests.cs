using System.Collections.Concurrent;
using System.Text;
using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser.Tests;

public sealed class GuestPipeBrowserHostTests
{
    private const string _secretName = "sid_name_zz";
    private const string _secretValue = "s3cr3t_value_qq";

    private static readonly BrowserLaunchSpec _spec = new("s1", "agent", BrowserGuestOs.Linux, "/usr/bin/chromium", ["--remote-debugging-pipe"]);

    private static BrowserCookieEntry[] Cookies() =>
        [new BrowserCookieEntry(_secretName, _secretValue, "example.com", "/", null)];

    private static BrowserHostOptions Fast() => new(TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task Start_ReturnsHandleWithSpecSessionAndGuestProcessId()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);

        var handle = await host.StartAsync(_spec, CancellationToken.None);

        Assert.Equal("s1", handle.SessionId);
        Assert.Equal("p1", handle.GuestProcessId);
    }

    [Fact]
    public async Task Start_RejectsEmptyExecutable()
    {
        var host = new GuestPipeBrowserHost(new FakePipe());
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(_spec with { ExecutablePath = " " }, CancellationToken.None));
    }

    [Fact]
    public async Task SetCookies_MatchingIdSucceeds_AndFrameIsNulTerminated()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.SetCookiesAsync(handle, Cookies(), CancellationToken.None);

        var written = Assert.Single(pipe.Writes);
        Assert.Equal(0, written[^1]);
        Assert.Contains("Network.setCookies", Encoding.UTF8.GetString(written));
    }

    [Fact]
    public async Task SetCookies_SkipsEventsAndOtherIds()
    {
        var pipe = new FakePipe();
        pipe.Preamble.Add("""{"method":"Target.targetCreated","params":{}}""");
        pipe.Preamble.Add("""{"id":999,"result":{}}""");
        pipe.Preamble.Add("not json");
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.SetCookiesAsync(handle, Cookies(), CancellationToken.None);
    }

    [Fact]
    public async Task SetCookies_ErrorResponse_ThrowsWithoutCookieData()
    {
        var pipe = new FakePipe { Reply = id => $$$"""{"id":{{{id}}},"error":{"code":-32000,"message":"bad {{{_secretName}}}={{{_secretValue}}}"}}""" };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetCookiesAsync(handle, Cookies(), CancellationToken.None));
        AssertClean(e);
    }

    [Fact]
    public async Task SetCookies_Timeout_ThrowsInvalidOperation()
    {
        var pipe = new FakePipe { Reply = null };
        var host = new GuestPipeBrowserHost(pipe, Fast());
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetCookiesAsync(handle, Cookies(), CancellationToken.None));
        AssertClean(e);
    }

    [Fact]
    public async Task SetCookies_CallerCancel_PropagatesCancellation()
    {
        var pipe = new FakePipe { Reply = null };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SetCookiesAsync(handle, Cookies(), cts.Token));
    }

    [Fact]
    public async Task SetCookies_PipeWriteThrows_WrapsWithoutLeaking()
    {
        var pipe = new FakePipe { WriteThrows = new IOException($"broken {_secretName} {_secretValue}") };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetCookiesAsync(handle, Cookies(), CancellationToken.None));
        AssertClean(e);
    }

    [Fact]
    public async Task SetCookies_PipeReadThrows_WrapsWithoutLeaking()
    {
        var pipe = new FakePipe { ReadThrows = new IOException($"broken {_secretValue}") };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetCookiesAsync(handle, Cookies(), CancellationToken.None));
        AssertClean(e);
    }

    [Fact]
    public async Task SetCookies_Empty_IsNoOp()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.SetCookiesAsync(handle, [], CancellationToken.None);

        Assert.Empty(pipe.Writes);
    }

    [Fact]
    public async Task SetCookies_UsesIncrementingRequestIds()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.SetCookiesAsync(handle, Cookies(), CancellationToken.None);
        await host.SetCookiesAsync(handle, Cookies(), CancellationToken.None);

        Assert.Equal([1, 2], pipe.WrittenIds());
    }

    [Fact]
    public async Task Stop_ClearsThenKills()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.StopAsync(handle, CancellationToken.None);

        Assert.Contains("Network.clearBrowserCookies", Encoding.UTF8.GetString(Assert.Single(pipe.Writes)));
        Assert.Equal(["p1"], pipe.Killed);
    }

    [Fact]
    public async Task Stop_KillsEvenWhenClearFails()
    {
        var pipe = new FakePipe { WriteThrows = new IOException("gone") };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.StopAsync(handle, CancellationToken.None);

        Assert.Equal(["p1"], pipe.Killed);
    }

    [Fact]
    public async Task Stop_KillsEvenWhenClearTimesOut()
    {
        var pipe = new FakePipe { Reply = null };
        var host = new GuestPipeBrowserHost(pipe, Fast());
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.StopAsync(handle, CancellationToken.None);

        Assert.Equal(["p1"], pipe.Killed);
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndUnknownHandleIsNoOp()
    {
        var pipe = new FakePipe();
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await host.StopAsync(handle, CancellationToken.None);
        await host.StopAsync(handle, CancellationToken.None);
        await host.StopAsync(new BrowserProcessHandle("x", "unknown"), CancellationToken.None);

        Assert.Equal(["p1"], pipe.Killed);
    }

    [Fact]
    public async Task ConcurrentCalls_OnOneHandle_AreSerialised()
    {
        var pipe = new FakePipe { ReadDelay = TimeSpan.FromMilliseconds(30) };
        var host = new GuestPipeBrowserHost(pipe);
        var handle = await host.StartAsync(_spec, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => host.SetCookiesAsync(handle, Cookies(), CancellationToken.None)));

        Assert.Equal(1, pipe.MaxInFlight);
        Assert.Equal([1, 2, 3, 4, 5], pipe.WrittenIds());
    }

    private static void AssertClean(Exception e)
    {
        var text = e.ToString();
        Assert.DoesNotContain(_secretName, text);
        Assert.DoesNotContain(_secretValue, text);
    }

    private sealed class FakePipe : IBrowserGuestPipe
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pending = new();
        private int _inFlight;
        private int _maxInFlight;

        public ConcurrentQueue<byte[]> WritesQueue { get; } = new();

        public IReadOnlyList<byte[]> Writes => [.. WritesQueue];

        public ConcurrentQueue<string> KilledQueue { get; } = new();

        public IReadOnlyList<string> Killed => [.. KilledQueue];

        public List<string> Preamble { get; } = [];

        public Func<int, string>? Reply { get; set; } = id => $$$"""{"id":{{{id}}},"result":{}}""";

        public Exception? WriteThrows { get; set; }

        public Exception? ReadThrows { get; set; }

        public TimeSpan ReadDelay { get; set; }

        public int MaxInFlight => _maxInFlight;

        public List<int> WrittenIds() =>
            [.. Writes.Select(w => System.Text.Json.JsonDocument.Parse(w.AsMemory(0, w.Length - 1)).RootElement.GetProperty("id").GetInt32())];

        public Task<string> StartProcessAsync(BrowserGuestOs os, string executable, IReadOnlyList<string> args, CancellationToken ct) =>
            Task.FromResult("p1");

        public Task WritePipeAsync(string guestProcessId, ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            if (WriteThrows is not null)
            {
                throw WriteThrows;
            }

            var bytes = frame.ToArray();
            WritesQueue.Enqueue(bytes);
            var q = _pending.GetOrAdd(guestProcessId, _ => new ConcurrentQueue<string>());
            foreach (var p in Preamble)
            {
                q.Enqueue(p);
            }

            if (Reply is not null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
                q.Enqueue(Reply(doc.RootElement.GetProperty("id").GetInt32()));
            }

            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight))
            {
                if (Interlocked.CompareExchange(ref _maxInFlight, now, seen) == seen)
                {
                    break;
                }
            }

            return Task.CompletedTask;
        }

        public async Task<byte[]> ReadPipeFrameAsync(string guestProcessId, CancellationToken ct)
        {
            if (ReadThrows is not null)
            {
                throw ReadThrows;
            }

            if (ReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(ReadDelay, ct);
            }

            var q = _pending.GetOrAdd(guestProcessId, _ => new ConcurrentQueue<string>());
            string? frame;
            while (!q.TryDequeue(out frame))
            {
                await Task.Delay(5, ct);
            }

            if (q.IsEmpty)
            {
                Interlocked.Decrement(ref _inFlight);
            }

            return Encoding.UTF8.GetBytes(frame);
        }

        public Task KillProcessTreeAsync(string guestProcessId, CancellationToken ct)
        {
            KilledQueue.Enqueue(guestProcessId);
            return Task.CompletedTask;
        }
    }
}
