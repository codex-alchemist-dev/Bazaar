using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Cbor;
using Bazaar.Wire.Codec.MemoryPack;
using Bazaar.Wire.Codec.MessagePack;
using Bazaar.Wire.Codec.Protobuf;

namespace Bazaar.Wire.Bench;

/// <summary>Measures one codec in this process and prints <c>RESULT</c> lines: <c>codec, run, corpus, metric, value</c>.</summary>
internal static class CodecRunner
{
    public static string[] CodecNames { get; } = ["json", "cbor", "msgpack", "protobuf", "memorypack"];

    private const int _rounds = 7;
    private static readonly TimeSpan _warmup = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan _round = TimeSpan.FromMilliseconds(250);

    public static IWireCodec Create(string name) => name switch
    {
        "json" => new JsonWireCodec(),
        "cbor" => new CborWireCodec(),
        "msgpack" => new MsgPackWireCodec(),
        "protobuf" => new ProtobufWireCodec(),
        "memorypack" => new MemoryPackWireCodec(),
        _ => throw new ArgumentException("unknown codec " + name),
    };

    public static int Run(string codecName, int run)
    {
        var codec = Create(codecName);
        var messages = Corpus.Messages();
        var snapshot = Corpus.Snapshot();
        var ts = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

        // Frames as a server would send them.
        Envelope Frame<T>(string type, EnvelopeKind kind, string? stream, long? n, T payload) =>
            new(1, type, kind, null, null, stream, n, ts, codec.EncodePayload(payload));

        var page = new GcPage([.. messages.Take(Corpus.PageSize)], true);
        var pageEnvelope = Frame("gc.page", EnvelopeKind.Response, null, null, page);
        var snapshotEnvelope = Frame("graph.snapshot", EnvelopeKind.Event, "graph", null, snapshot);

        var replayFrames = new byte[messages.Count][];
        var writer = new ArrayBufferWriter<byte>(1 << 20);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            writer.ResetWrittenCount();
            codec.Encode(Frame(WireTypes.GcMessage, EnvelopeKind.Event, "gc:" + m.GcId.ToString("N"), m.N, m), writer);
            replayFrames[i] = writer.WrittenSpan.ToArray();
        }

        writer.ResetWrittenCount();
        codec.Encode(pageEnvelope, writer);
        var pageFrame = writer.WrittenSpan.ToArray();
        writer.ResetWrittenCount();
        codec.Encode(snapshotEnvelope, writer);
        var snapshotFrame = writer.WrittenSpan.ToArray();

        // Correctness: a decode must give the input back, or the timing means nothing.
        var checkPage = codec.DecodePayload<GcPage>(codec.Decode(pageFrame));
        if (checkPage.Items.Count != Corpus.PageSize || !Same(checkPage.Items[199], page.Items[199])) throw new InvalidOperationException(codecName + ": page did not round trip");
        var checkSnapshot = codec.DecodePayload<GraphSnapshot>(codec.Decode(snapshotFrame));
        if (checkSnapshot.Nodes.Count != Corpus.NodeCount || checkSnapshot.Edges.Count != Corpus.EdgeCount || checkSnapshot.Edges[^1] != snapshot.Edges[^1] || checkSnapshot.Nodes[^1] != snapshot.Nodes[^1] || checkSnapshot.Edges[7] != snapshot.Edges[7]) throw new InvalidOperationException(codecName + ": snapshot did not round trip");
        var checkMessage = codec.DecodePayload<WireGcMessage>(codec.Decode(replayFrames[^1]));
        if (!Same(checkMessage, messages[^1])) throw new InvalidOperationException(codecName + ": message did not round trip");

        void Emit(string corpus, string metric, double value) =>
            Console.WriteLine(string.Join('\t', "RESULT", codecName, run.ToString(CultureInfo.InvariantCulture), corpus, metric, value.ToString("R", CultureInfo.InvariantCulture)));

        // Bytes. Raw sizes are deterministic and checked in every run; compression is measured in run 1 only.
        var replayTotal = replayFrames.Sum(f => (long)f.Length);
        Emit("page200", "bytes_raw", pageFrame.Length);
        Emit("replay10k", "bytes_raw", replayTotal);
        Emit("replay10k", "bytes_per_frame", (double)replayTotal / replayFrames.Length);
        Emit("snapshot", "bytes_raw", snapshotFrame.Length);
        if (run == 1)
        {
            Emit("page200", "bytes_gzip", Compress(pageFrame, gzip: true));
            Emit("page200", "bytes_brotli", Compress(pageFrame, gzip: false));
            Emit("snapshot", "bytes_gzip", Compress(snapshotFrame, gzip: true));
            Emit("snapshot", "bytes_brotli", Compress(snapshotFrame, gzip: false));
            var joined = new byte[replayTotal];
            var at = 0;
            foreach (var f in replayFrames)
            {
                f.CopyTo(joined, at);
                at += f.Length;
            }

            Emit("replay10k", "bytes_gzip", Compress(joined, gzip: true));
            Emit("replay10k", "bytes_brotli", Compress(joined, gzip: false));
        }

        // Time and allocations per frame.
        var sink = new ArrayBufferWriter<byte>(1 << 22);

        void EncodePage()
        {
            sink.ResetWrittenCount();
            var e = pageEnvelope with { P = codec.EncodePayload(page) };
            codec.Encode(e, sink);
        }

        void DecodePage()
        {
            var e = codec.Decode(pageFrame);
            _ = codec.DecodePayload<GcPage>(e);
        }

        void EncodeSnapshot()
        {
            sink.ResetWrittenCount();
            var e = snapshotEnvelope with { P = codec.EncodePayload(snapshot) };
            codec.Encode(e, sink);
        }

        void DecodeSnapshot()
        {
            var e = codec.Decode(snapshotFrame);
            _ = codec.DecodePayload<GraphSnapshot>(e);
        }

        void EncodeReplay()
        {
            foreach (var m in messages)
            {
                sink.ResetWrittenCount();
                var e = new Envelope(1, WireTypes.GcMessage, EnvelopeKind.Event, null, null, null, m.N, ts, codec.EncodePayload(m));
                codec.Encode(e, sink);
            }
        }

        void DecodeReplay()
        {
            foreach (var f in replayFrames)
            {
                var e = codec.Decode(f);
                _ = codec.DecodePayload<WireGcMessage>(e);
            }
        }

        Time("page200", "encode", EncodePage, 1, Emit);
        Time("page200", "decode", DecodePage, 1, Emit);
        Time("replay10k", "encode", EncodeReplay, messages.Count, Emit);
        Time("replay10k", "decode", DecodeReplay, replayFrames.Length, Emit);
        Time("snapshot", "encode", EncodeSnapshot, 1, Emit);
        Time("snapshot", "decode", DecodeSnapshot, 1, Emit);
        return 0;
    }

    private static bool Same(WireGcMessage a, WireGcMessage b) =>
        a.GcId == b.GcId && a.N == b.N && a.Ev == b.Ev && a.MessageId == b.MessageId && a.AuthorId == b.AuthorId && a.Text == b.Text && a.Ts == b.Ts
        && a.Edited == b.Edited && a.ReplyToN == b.ReplyToN && a.Mentions.SequenceEqual(b.Mentions);

    private static long Compress(byte[] data, bool gzip)
    {
        using var output = new MemoryStream();
        using (Stream z = gzip ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true) : new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.Length;
    }

    /// <summary>Warm up, then run seven rounds; emit the median round's microseconds per frame and bytes allocated per frame, plus the fastest and slowest round.</summary>
    private static void Time(string corpus, string op, Action pass, int framesPerPass, Action<string, string, double> emit)
    {
        var sw = Stopwatch.StartNew();
        var warmPasses = 0;
        while (sw.Elapsed < _warmup || warmPasses < 3)
        {
            pass();
            warmPasses++;
        }

        var passTicks = sw.Elapsed.TotalSeconds / warmPasses;
        var reps = Math.Max(1, (int)Math.Ceiling(_round.TotalSeconds / passTicks));
        var times = new double[_rounds];
        var allocs = new double[_rounds];
        for (var r = 0; r < _rounds; r++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < reps; i++) pass();
            var elapsed = Stopwatch.GetElapsedTime(start);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var frames = (double)reps * framesPerPass;
            times[r] = elapsed.TotalMicroseconds / frames;
            allocs[r] = (after - before) / frames;
        }

        Array.Sort(times);
        Array.Sort(allocs);
        emit(corpus, op + "_us_per_frame_median", times[_rounds / 2]);
        emit(corpus, op + "_us_per_frame_min", times[0]);
        emit(corpus, op + "_us_per_frame_max", times[^1]);
        emit(corpus, op + "_alloc_bytes_per_frame", allocs[_rounds / 2]);
    }
}
