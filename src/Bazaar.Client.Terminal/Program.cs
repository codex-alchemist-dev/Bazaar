using System.Threading.Channels;
using Bazaar.Client.Core.Wire;
using Bazaar.Client.Terminal;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(TerminalArgs.Usage);
    return 0;
}

TerminalOptions options;
try
{
    options = TerminalArgs.Parse(args, Environment.GetEnvironmentVariable);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(TerminalArgs.Usage);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Console writes come from the receive loop, the status callback and the input loop; one lock keeps lines whole.
var gate = new object();
bool closing = false;
void Write(string line)
{
    lock (gate)
    {
        if (!closing) Console.WriteLine(line);
    }
}

var ownSeqs = new HashSet<long>();
var transport = new StatusTransport(new WebSocketWireTransportFactory(options.Server), line => Write(MessageFormatter.Status(line)));
var client = new WireClient(
    transport,
    new JsonWireCodec(),
    new InMemoryCursorStore(),
    new WireClientOptions
    {
        DeviceId = $"terminal-{Environment.MachineName}-{options.Name}",
        DeviceClass = "terminal",
        ClientBuild = "bazaar-terminal",
        Credential = options.Token,
    },
    TimeProvider.System);

// Replays from the start (no stored cursor), so the whole history prints before live messages.
await client.SubscribeAsync(WireStreams.Gc(options.GcId), cts.Token);
Task run = client.RunAsync(cts.Token);
_ = run.ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);

Task receive = Task.Run(async () =>
{
    try
    {
        await foreach (WireGcMessage m in client.Messages.ReadAllAsync(cts.Token))
        {
            if (m.GcId != options.GcId) continue;
            bool own;
            lock (ownSeqs) own = ownSeqs.Contains(m.N);
            Write(MessageFormatter.Format(m, MessageFormatter.Resolve(m, own, options.Name, options.Members), DateTimeOffset.Now.Offset));
        }
    }
    catch (OperationCanceledException)
    {
    }
});

Write(MessageFormatter.Status("connecting; type a message, /quit to leave"));

// Sends go through one ordered queue, so a send waiting on a dead socket never blocks typing or /quit.
var outbound = Channel.CreateUnbounded<string>();
Task sender = Task.Run(async () =>
{
    try
    {
        await foreach (string text in outbound.Reader.ReadAllAsync(cts.Token))
        {
            try
            {
                var sent = await client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(options.GcId, text, null, null), cts.Token);
                lock (ownSeqs) ownSeqs.Add(sent.N);
            }
            catch (WireRequestException ex)
            {
                Write(MessageFormatter.Status($"send failed: {ex.Error.Code}"));
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
});

try
{
    while (!cts.IsCancellationRequested)
    {
        string? line = await Console.In.ReadLineAsync(cts.Token);
        if (line is null || line.Trim() == "/quit") break;
        if (!string.IsNullOrWhiteSpace(line)) outbound.Writer.TryWrite(line);
    }
}
catch (OperationCanceledException)
{
}

// Let queued sends go out, but never wait long on a dead socket; Ctrl+C skips the wait.
outbound.Writer.TryComplete();
try
{
    await sender.WaitAsync(TimeSpan.FromSeconds(5));
}
catch (TimeoutException)
{
}
cts.Cancel();
try
{
    await Task.WhenAll(run, receive, sender);
}
catch (OperationCanceledException)
{
}

lock (gate)
{
    closing = true;
}

WireClientStopReason? stopped = client.Stopped;
if (stopped is null or WireClientStopReason.Cancelled)
{
    Console.WriteLine(MessageFormatter.Status("bye"));
    return 0;
}

Console.WriteLine(MessageFormatter.Status($"stopped: {stopped}"));
return 1;
