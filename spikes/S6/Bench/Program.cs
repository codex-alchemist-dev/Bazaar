using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using Spike.S6.Wire;

// Bytes and time for a 200-message page (one frame) and a 10k replay (one frame per message).
// Single process, Release, warm-up first. Output is one line per measure, for the spike record.
IWireCodec[] codecs = [new JsonWireCodec(), new ProtoWireCodec(), new ProtoJsonWireCodec()];
var all = Corpus.Messages(10_000);
var page = all.Take(200).ToArray();
int runs = 3;

foreach (var c in codecs)
{
    // Page: 200 messages in one gc.page event.
    var pageFrame = EncodePage(c, page);
    // Replay: 10k events, one frame each.
    var replayFrames = all.Select(m => EncodeOne(c, m)).ToArray();
    long replayBytes = replayFrames.Sum(f => f.Length);
    Console.WriteLine($"{c.Name} page200 bytes={pageFrame.Length} gzip={Compress(pageFrame, true)} brotli={Compress(pageFrame, false)}");
    Console.WriteLine($"{c.Name} replay10k bytes={replayBytes} avg={replayBytes / 10_000.0:F1}");
    for (int i = 1; i <= runs; i++)
    {
        double pe = Time(() => EncodePage(c, page), 200), pd = Time(() => DecodePage(c, pageFrame), 200);
        double re = Time(() => { foreach (var m in all) EncodeOne(c, m); }, 20), rd = Time(() => { foreach (var f in replayFrames) DecodeOne(c, f); }, 20);
        Console.WriteLine($"{c.Name} run{i} page_encode_us={pe:F1} page_decode_us={pd:F1} replay_encode_ms={re / 1000:F2} replay_decode_ms={rd / 1000:F2}");
    }
}

static byte[] EncodeOne(IWireCodec c, GcMessage m)
{
    var pw = new ArrayBufferWriter<byte>(512);
    c.EncodePayload(m, pw);
    var w = new ArrayBufferWriter<byte>(768);
    c.Encode(new Envelope(1, "gc.message", Kind.Evt, null, null, "gc:gc-0001", m.N, m.Ts, pw.WrittenMemory), w);
    return w.WrittenSpan.ToArray();
}

static GcMessage DecodeOne(IWireCodec c, byte[] f) => c.DecodePayload<GcMessage>(c.Decode(f));

static byte[] EncodePage(IWireCodec c, GcMessage[] page)
{
    var pw = new ArrayBufferWriter<byte>(64_000);
    c.EncodePayload(new GcPage(page), pw);
    var w = new ArrayBufferWriter<byte>(64_000);
    c.Encode(new Envelope(1, "gc.page", Kind.Evt, null, null, "gc:gc-0001", null, DateTimeOffset.UnixEpoch, pw.WrittenMemory), w);
    return w.WrittenSpan.ToArray();
}

static void DecodePage(IWireCodec c, byte[] f) => c.DecodePayload<GcPage>(c.Decode(f));

static double Time(Action a, int iterations)
{
    for (int i = 0; i < Math.Max(2, iterations / 10); i++) a(); // warm-up
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++) a();
    sw.Stop();
    return sw.Elapsed.TotalMicroseconds / iterations;
}

static int Compress(byte[] data, bool gzip)
{
    using var ms = new MemoryStream();
    using (Stream z = gzip ? new GZipStream(ms, CompressionLevel.Optimal, true) : new BrotliStream(ms, CompressionLevel.Optimal, true))
        z.Write(data);
    return (int)ms.Length;
}
