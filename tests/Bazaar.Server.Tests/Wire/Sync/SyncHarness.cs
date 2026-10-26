using System.Collections.Concurrent;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Sync;

#pragma warning disable CA1822 // test helpers read better as instance calls

namespace Bazaar.Server.Tests.Wire.Sync;

/// <summary>A fake log, dispatcher and engine, plus a helper that plays the connection's socket writer.</summary>
internal sealed class SyncHarness : IDisposable
{
    public static readonly Guid Gc = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public SyncHarness(SyncOptions? options = null, TimeSpan? sweep = null)
    {
        Options = options ?? new SyncOptions { ReplayPageSize = 10, LiveBufferMax = 50 };
        Log = new FakeGcLog();
        Log.CreateGc(Gc);
        Log.AddMember(Alice, Gc);
        Dispatcher = new GcDispatcher(Log, Options);
        Engine = new SubscriptionEngine(Log, Log, Dispatcher, Options);
        _run = Dispatcher.RunAsync(Log, sweep, _stop.Token);
    }

    public SyncOptions Options { get; }

    public FakeGcLog Log { get; }

    public GcDispatcher Dispatcher { get; }

    public SubscriptionEngine Engine { get; }

    public Connection Connect(Guid? principal = null, int? queueMax = null, bool paused = false) =>
        new(Engine, principal ?? Alice, queueMax ?? Options.OutboundQueueMax, paused);

    public void Dispose()
    {
        _stop.Cancel();
        try { _run.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        Dispatcher.Dispose();
        _stop.Dispose();
    }

    internal sealed class Connection : IDisposable
    {
        private static readonly JsonWireCodec _codec = new();
        private readonly BoundedOutbound _outbound;
        private readonly CancellationTokenSource _cts = new();
        private Task _reader = Task.CompletedTask;
        private readonly ConcurrentQueue<Envelope> _frames = new();

        public Connection(SubscriptionEngine engine, Guid principal, int queueMax, bool paused)
        {
            _outbound = new BoundedOutbound(queueMax);
            Session = engine.CreateSession(principal, _codec, _outbound);
            if (!paused)
            {
                Resume();
            }
        }

        public SubscriptionSession Session { get; }

        /// <summary>Starts the socket writer loop, draining the queue.</summary>
        public void Resume()
        {
            _reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var f in _outbound.ReadAllAsync(_cts.Token))
                    {
                        _frames.Enqueue(f);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public IReadOnlyList<Envelope> Frames => [.. _frames];

        public IReadOnlyList<WireGcMessage> Messages =>
            [.. _frames.Where(f => f.T == WireTypes.GcMessage).Select(f => _codec.DecodePayload<WireGcMessage>(f))];

        public IReadOnlyList<Envelope> Of(string type) => [.. _frames.Where(f => f.T == type)];

        public T Payload<T>(Envelope e) => _codec.DecodePayload<T>(e);

        public async Task<SubscribeResult> SubscribeAsync(long after = 0, long editAfter = 0, Guid? gc = null)
        {
            var outcome = await Session.SubscribeAsync(
                new Subscribe([new StreamCursor(WireStreams.Gc(gc ?? Gc), after, editAfter)]), CancellationToken.None);
            await outcome.StartAsync();
            await SettleAsync();
            return outcome.Result;
        }

        /// <summary>Waits until the writer loop has taken everything queued (a no-op while the connection is paused).</summary>
        public async Task SettleAsync()
        {
            if (_reader.IsCompleted)
            {
                return;
            }

            await WaitAsync(() => _outbound.Depth == 0);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        public async Task WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!condition())
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("condition not met");
                }

                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        public void Dispose()
        {
            Session.Dispose();
            _outbound.Complete();
            _cts.Cancel();
            try { _reader.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            _outbound.Dispose();
            _cts.Dispose();
        }
    }
}
