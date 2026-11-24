using System.Diagnostics;
using System.Globalization;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Bench;

/// <summary>Settings of one transport process run.</summary>
/// <param name="Condition">Label of the network condition the workflow set up (informational; the harness cannot inject loss).</param>
/// <param name="Duration">Length of the throughput window.</param>
/// <param name="Rate">Messages per second in the throughput window.</param>
/// <param name="WarmConnects">Connects after the first, for the warm figure.</param>
/// <param name="Pages">Replay pages after the first, for the warm figure.</param>
/// <param name="Reconnects">Forced drops measured.</param>
/// <param name="StepTimeout">Longest wait for one connect, one page or one reconnect.</param>
internal sealed record TransportRunOptions(string Condition, TimeSpan Duration, int Rate, int WarmConnects, int Pages, int Reconnects, TimeSpan StepTimeout)
{
    public static TransportRunOptions Default { get; } = new("clean", TimeSpan.FromSeconds(10), 1000, 10, 10, 5, TimeSpan.FromSeconds(30));
}

/// <summary>
/// Measures one transport in this process (spike S-WTX, doc 90 section 10): the real server and client modules over
/// loopback by IP, the JSON codec, client and server in the same process. Prints <c>RESULT</c> lines
/// (<c>transport, run, condition, metric, value</c>), <c>FAIL</c> lines (the same plus a reason) and <c>INFO</c> lines.
/// </summary>
internal static class TransportRunner
{
    private const int _pageFrames = 200;
    private static readonly byte[] _request = [(byte)'R'];

    public static async Task<int> RunAsync(string name, int run, TransportRunOptions options)
    {
        TransportScenario scenario;
        try
        {
            scenario = TransportScenario.Create(name);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        void Result(string metric, double value) =>
            Console.WriteLine(string.Join('\t', "RESULT", name, run.ToString(CultureInfo.InvariantCulture), options.Condition, metric, value.ToString("R", CultureInfo.InvariantCulture)));
        void Fail(string metric, string reason) =>
            Console.WriteLine(string.Join('\t', "FAIL", name, run.ToString(CultureInfo.InvariantCulture), options.Condition, metric, reason.ReplaceLineEndings(" ").Replace('\t', ' ')));
        void Info(string key, string value) =>
            Console.WriteLine(string.Join('\t', "INFO", name, run.ToString(CultureInfo.InvariantCulture), options.Condition, key, value));

        Info("runtime", $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {Environment.ProcessorCount} logical CPUs");
        Info("drop_method", scenario.DropMethod);
        await using (scenario.ConfigureAwait(false))
        {
            if (scenario.UnsupportedReason is { } unsupported)
            {
                foreach (var (metric, _) in TransportStats.Metrics) Fail(metric, unsupported);
                return 0;
            }

            var frames = BuildFrames();
            try
            {
                await scenario.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                foreach (var (metric, _) in TransportStats.Metrics) Fail(metric, "listener did not start: " + Short(ex));
                return 0;
            }

            // Metric group 1: cold connect, then the first replay page on that same connection.
            ConnectionPair? pair = null;
            try
            {
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(options.StepTimeout);
                pair = await scenario.ConnectAsync(cts.Token).ConfigureAwait(false);
                var coldMs = sw.Elapsed.TotalMilliseconds;
                if (scenario.ExpectedProtocol is { } expected && scenario.ObservedProtocol != expected)
                {
                    var reason = $"negotiated {scenario.ObservedProtocol ?? "unknown"}, expected {expected}; not a {scenario.Name} row";
                    foreach (var (metric, _) in TransportStats.Metrics) Fail(metric, reason);
                    await pair.DisposeAsync().ConfigureAwait(false);
                    return 0;
                }

                Result("connect_cold_ms", coldMs);
                Info("protocol", scenario.ObservedProtocol ?? scenario.Name);
            }
            catch (Exception ex)
            {
                foreach (var (metric, _) in TransportStats.Metrics) Fail(metric, "connect failed: " + Short(ex));
                return 0;
            }

            await using (pair.ConfigureAwait(false))
            {
                await Guarded("replay_first_page_ms", Fail, async () =>
                {
                    Result("replay_first_page_ms", await PageAsync(pair, frames, options.StepTimeout).ConfigureAwait(false));
                    var times = new List<double>();
                    for (var i = 0; i < options.Pages; i++) times.Add(await PageAsync(pair, frames, options.StepTimeout).ConfigureAwait(false));
                    Result("replay_page_ms", TransportStats.Median(times));
                }, "replay_page_ms").ConfigureAwait(false);

                await Guarded("connect_warm_ms", Fail, async () =>
                {
                    var times = new List<double>();
                    for (var i = 0; i < options.WarmConnects; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        using var cts = new CancellationTokenSource(options.StepTimeout);
                        var extra = await scenario.ConnectAsync(cts.Token).ConfigureAwait(false);
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        await extra.DisposeAsync().ConfigureAwait(false);
                    }

                    Result("connect_warm_ms", TransportStats.Median(times));
                }).ConfigureAwait(false);

                await Guarded("throughput_msgs_per_s", Fail, async () =>
                {
                    var t = await ThroughputAsync(pair, frames, options).ConfigureAwait(false);
                    Result("throughput_msgs_per_s", t.Delivered / t.Seconds);
                    Result("delivered_fraction", (double)t.Delivered / t.Sent);
                    Result("latency_p50_ms", t.P50Ms);
                    Result("latency_p99_ms", t.P99Ms);
                    Result("cpu_ms_per_1000_msgs", t.CpuMs / (t.Delivered / 1000.0));
                }, "delivered_fraction", "latency_p50_ms", "latency_p99_ms", "cpu_ms_per_1000_msgs").ConfigureAwait(false);
            }

            await Guarded("reconnect_total_ms", Fail, async () =>
            {
                var detect = new List<double>();
                var total = new List<double>();
                for (var i = 0; i < options.Reconnects; i++)
                {
                    using var cts = new CancellationTokenSource(options.StepTimeout);
                    var first = await scenario.ConnectAsync(cts.Token).ConfigureAwait(false);
                    var sw = Stopwatch.StartNew();
                    await first.DropAsync().ConfigureAwait(false);
                    var seen = await first.Client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (!seen.IsClosed) throw new InvalidOperationException("the client got a frame instead of a close after the drop");
                    detect.Add(sw.Elapsed.TotalMilliseconds);

                    // The client's reconnect: connect again at once (no backoff), then the first frame the server sends on the new connection.
                    ConnectionPair next;
                    while (true)
                    {
                        try
                        {
                            next = await scenario.ConnectAsync(cts.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await Task.Delay(50, cts.Token).ConfigureAwait(false);
                        }
                    }

                    await next.Server.SendAsync(frames[0], cts.Token).ConfigureAwait(false);
                    var got = await next.Client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (got.IsClosed) throw new InvalidOperationException("the new connection closed before its first frame");
                    total.Add(sw.Elapsed.TotalMilliseconds);
                    await next.DisposeAsync().ConfigureAwait(false);
                    await first.DisposeAsync().ConfigureAwait(false);
                }

                Result("reconnect_detect_ms", TransportStats.Median(detect));
                Result("reconnect_total_ms", TransportStats.Median(total));
            }, "reconnect_detect_ms").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task Guarded(string metric, Action<string, string> fail, Func<Task> body, params string[] alsoFails)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fail(metric, Short(ex));
            foreach (var m in alsoFails) fail(m, Short(ex));
        }
    }

    private static string Short(Exception ex) => (ex is OperationCanceledException ? "timed out: " : string.Empty) + ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message);

    /// <summary>The first 200 of the S6 replay corpus as JSON <c>gc.message</c> frames, then the rest, as a server would send them.</summary>
    internal static byte[][] BuildFrames()
    {
        var codec = new JsonWireCodec();
        var messages = Corpus.Messages();
        var ts = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var writer = new System.Buffers.ArrayBufferWriter<byte>(1 << 16);
        var frames = new byte[messages.Count][];
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            writer.ResetWrittenCount();
            codec.Encode(new Envelope(1, WireTypes.GcMessage, EnvelopeKind.Event, null, null, "gc:" + m.GcId.ToString("N"), m.N, ts, codec.EncodePayload(m)), writer);
            frames[i] = writer.WrittenSpan.ToArray();
        }

        return frames;
    }

    /// <summary>Client sends a request, the server answers with a page of 200 <c>gc.message</c> frames; returns ms from the request to the 200th frame.</summary>
    private static async Task<double> PageAsync(ConnectionPair pair, byte[][] frames, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sw = Stopwatch.StartNew();
        await pair.Client.SendAsync(_request, cts.Token).ConfigureAwait(false);
        var seen = await pair.Server.ReceiveAsync(cts.Token).ConfigureAwait(false);
        if (seen.IsClosed) throw new InvalidOperationException("the server end closed before the request arrived");
        var sender = Task.Run(async () =>
        {
            for (var i = 0; i < _pageFrames; i++) await pair.Server.SendAsync(frames[i], cts.Token).ConfigureAwait(false);
        }, cts.Token);
        for (var i = 0; i < _pageFrames; i++)
        {
            var got = await pair.Client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (got.IsClosed) throw new InvalidOperationException("the connection closed during the page");
        }

        await sender.ConfigureAwait(false);
        return sw.Elapsed.TotalMilliseconds;
    }

    private sealed record Throughput(int Sent, int Delivered, double Seconds, double P50Ms, double P99Ms, double CpuMs);

    /// <summary>
    /// The server sends <c>Rate</c> frames a second for <c>Duration</c> (each frame has a scheduled send time, a late sender catches up in a burst);
    /// the client records when each arrives. Latency is arrival minus the scheduled send time. CPU is the process total (both ends, the pacing and the recording).
    /// </summary>
    private static async Task<Throughput> ThroughputAsync(ConnectionPair pair, byte[][] frames, TransportRunOptions options)
    {
        var total = (int)(options.Rate * options.Duration.TotalSeconds);
        var ticksPerMessage = Stopwatch.Frequency / (double)options.Rate;
        var arrivals = new long[total];
        var delivered = 0;
        using var cts = new CancellationTokenSource(options.Duration + options.StepTimeout);
        var process = Process.GetCurrentProcess();
        GC.Collect();
        var cpuBefore = process.TotalProcessorTime;
        var start = Stopwatch.GetTimestamp();
        var sender = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
            {
                var due = start + (long)(i * ticksPerMessage);
                var wait = TimeSpan.FromSeconds((due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                if (wait > TimeSpan.FromMilliseconds(1)) await Task.Delay(wait, cts.Token).ConfigureAwait(false);
                await pair.Server.SendAsync(frames[i % frames.Length], cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
        try
        {
            while (delivered < total)
            {
                var got = await pair.Client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (got.IsClosed) break;
                arrivals[delivered++] = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException)
        {
            // Frames still missing when the window plus the step timeout ran out stay undelivered.
        }

        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        try
        {
            await sender.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The send side ends with the receive side.
        }

        if (delivered == 0) throw new InvalidOperationException("no frame arrived in the throughput window");
        var latencies = new double[delivered];
        for (var i = 0; i < delivered; i++)
        {
            latencies[i] = Math.Max(0, (arrivals[i] - (start + (long)(i * ticksPerMessage))) * 1000.0 / Stopwatch.Frequency);
        }

        return new Throughput(total, delivered, (arrivals[delivered - 1] - start) / (double)Stopwatch.Frequency,
            TransportStats.Percentile(latencies, 50), TransportStats.Percentile(latencies, 99), cpu);
    }
}
