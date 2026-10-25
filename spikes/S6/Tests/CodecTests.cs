using System.Buffers;
using System.Text;
using Spike.S6.Wire;

namespace Spike.S6.Tests;

public class CodecTests
{
    public static TheoryData<IWireCodec> Codecs => new() { new JsonWireCodec(), new ProtoWireCodec() };

    private static byte[] Frame(IWireCodec c, string type, Kind k, object payload, string? s = null, long? n = null, string? id = null)
    {
        var pw = new ArrayBufferWriter<byte>();
        switch (payload)
        {
            case Hello h: c.EncodePayload(h, pw); break;
            case Subscribe x: c.EncodePayload(x, pw); break;
            case GcMessage g: c.EncodePayload(g, pw); break;
            case GcPage g: c.EncodePayload(g, pw); break;
            case Lagged l: c.EncodePayload(l, pw); break;
            case GcMessageV11 g: c.EncodePayload(g, pw); break;
            default: throw new NotSupportedException();
        }
        var env = new Envelope(1, type, k, id, null, s, n, new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero), pw.WrittenMemory);
        var w = new ArrayBufferWriter<byte>();
        c.Encode(env, w);
        return w.WrittenSpan.ToArray();
    }

    [Theory, MemberData(nameof(Codecs))]
    public void Hello_round_trips(IWireCodec c)
    {
        var hello = new Hello(1, 1, ["json", "protobuf"], "dev-1", "windows", "0.1.0", [new Cursor("gc:1", 10, 12)]);
        var env = c.Decode(Frame(c, "hello", Kind.Req, hello, id: "r1"));
        Assert.Equal("hello", env.T);
        Assert.Equal(Kind.Req, env.K);
        Assert.Equal("r1", env.Id);
        var back = c.DecodePayload<Hello>(env);
        Assert.Equal(hello.DeviceId, back.DeviceId);
        Assert.Equal(hello.Codecs, back.Codecs);
        Assert.Equal(hello.Cursors, back.Cursors);
    }

    [Theory, MemberData(nameof(Codecs))]
    public void Subscribe_and_lagged_round_trip(IWireCodec c)
    {
        var sub = new Subscribe([new StreamRef("gc:1", 5, 6), new StreamRef("presence", 0, 0)]);
        Assert.Equal(sub.Streams, c.DecodePayload<Subscribe>(c.Decode(Frame(c, "subscribe", Kind.Req, sub))).Streams);
        var lag = new Lagged("gc:1", 99);
        Assert.Equal(lag, c.DecodePayload<Lagged>(c.Decode(Frame(c, "lagged", Kind.Evt, lag, s: "gc:1"))));
    }

    [Theory, MemberData(nameof(Codecs))]
    public void GcMessage_round_trips_with_n_and_ev(IWireCodec c)
    {
        foreach (var m in Corpus.Messages(50))
        {
            var env = c.Decode(Frame(c, "gc.message", Kind.Evt, m, s: "gc:gc-0001", n: m.N));
            Assert.Equal(m.N, env.N);
            var back = c.DecodePayload<GcMessage>(env);
            Assert.Equal(m.Text, back.Text);
            Assert.Equal(m.Ev, back.Ev);
            Assert.Equal(m.Ts, back.Ts);
            Assert.Equal(m.Mentions, back.Mentions);
        }
    }

    [Theory, MemberData(nameof(Codecs))]
    public void Added_payload_field_is_ignored_by_old_decoder(IWireCodec c)
    {
        var v11 = new GcMessageV11("gc-1", 3, 3, "m1", "a1", "hi", DateTimeOffset.UnixEpoch, false, [], "thread-9");
        var env = c.Decode(Frame(c, "gc.message", Kind.Evt, v11, s: "gc:gc-1", n: 3));
        var old = c.DecodePayload<GcMessage>(env); // v1.0 reader sees a v1.1 payload
        Assert.Equal("hi", old.Text);
        Assert.Equal("thread-9", c.DecodePayload<GcMessageV11>(env).ThreadId);
    }

    [Theory, MemberData(nameof(Codecs))]
    public void Old_payload_decodes_in_new_reader_with_default(IWireCodec c)
    {
        var m = Corpus.Messages(1)[0];
        var env = c.Decode(Frame(c, "gc.message", Kind.Evt, m, n: m.N));
        Assert.Null(c.DecodePayload<GcMessageV11>(env).ThreadId);
    }

    [Theory, MemberData(nameof(Codecs))]
    public void Unknown_message_type_decodes_envelope_and_keeps_payload_opaque(IWireCodec c)
    {
        var env = c.Decode(Frame(c, "future.thing", Kind.Evt, new Lagged("x", 1)));
        Assert.Equal("future.thing", env.T);
        Assert.False(env.P.IsEmpty); // the dispatcher can answer err unsupported without parsing p
    }

    [Fact]
    public void Json_unknown_envelope_field_is_ignored()
    {
        var c = new JsonWireCodec();
        var frame = """{"v":1,"t":"gc.message","k":"evt","zz":{"a":[1,2]},"n":7,"ts":"2026-10-25T05:00:00+00:00","p":{"gcId":"g","n":7,"ev":7,"messageId":"m","authorId":"a","text":"t","ts":"2026-10-25T05:00:00+00:00","edited":false,"mentions":[],"future":1}}"""u8;
        var env = c.Decode(frame);
        Assert.Equal(7, env.N);
        Assert.Equal("t", c.DecodePayload<GcMessage>(env).Text);
    }

    [Fact]
    public void Proto_unknown_envelope_field_is_ignored()
    {
        // Envelope with an extra field 15 (varint) appended after the known fields.
        var c = new ProtoWireCodec();
        var good = Frame(c, "lagged", Kind.Evt, new Lagged("s", 1));
        byte[] extended = [.. good, 0x78, 0x2A]; // field 15, wire type 0, value 42
        Assert.Equal("lagged", c.Decode(extended).T);
    }

    [Fact]
    public void Json_golden_bytes_are_stable_and_human_readable()
    {
        var c = new JsonWireCodec();
        var hello = new Lagged("gc:1", 99);
        var pw = new ArrayBufferWriter<byte>();
        c.EncodePayload(hello, pw);
        var env = new Envelope(1, "lagged", Kind.Evt, null, null, "gc:1", null, DateTimeOffset.UnixEpoch, pw.WrittenMemory);
        var w = new ArrayBufferWriter<byte>();
        c.Encode(env, w);
        Assert.Equal("""{"v":1,"t":"lagged","k":"evt","s":"gc:1","ts":"1970-01-01T00:00:00+00:00","p":{"s":"gc:1","lastSent":99}}""",
            Encoding.UTF8.GetString(w.WrittenSpan));
    }
}
