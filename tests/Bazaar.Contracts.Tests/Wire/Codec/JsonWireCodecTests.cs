using System.Buffers;
using System.Text;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Contracts.Tests.Wire.Codec;

public class JsonWireCodecTests
{
    private static readonly JsonWireCodec _codec = new();
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset _at = new(2026, 10, 25, 5, 36, 0, TimeSpan.Zero);

    private static string EncodeText(in Envelope e)
    {
        var w = new ArrayBufferWriter<byte>();
        _codec.Encode(e, w);
        return Encoding.UTF8.GetString(w.WrittenSpan);
    }

    private static Envelope Decode(string json) => _codec.Decode(Encoding.UTF8.GetBytes(json));

    private static Envelope Event() => new(1, WireTypes.GcMessage, EnvelopeKind.Event, null, null, WireStreams.Gc(_gc), 12, _at,
        _codec.EncodePayload(new WireGcMessage(_gc, 12, 40, _gc, _author, "héllo \"world\"", _at, false, [], null)));

    [Fact]
    public void Codec_identifies_itself_as_json_text()
    {
        Assert.Equal("json", _codec.Name);
        Assert.False(_codec.IsBinary);
    }

    [Fact]
    public void Event_frame_matches_golden_bytes()
    {
        GoldenFile.Check("event-gc-message.json", EncodeText(Event()));
    }

    [Fact]
    public void Request_frame_matches_golden_bytes()
    {
        var req = new Envelope(1, WireTypes.GcSend, EnvelopeKind.Request, "req-7", null, null, null, null,
            _codec.EncodePayload(new GcSend(_gc, "hi", null, null)));

        GoldenFile.Check("request-gc-send.json", EncodeText(req));
    }

    [Fact]
    public void Error_frame_matches_golden_bytes()
    {
        var err = new Envelope(1, WireTypes.GcSend, EnvelopeKind.Error, null, "req-7", null, null, _at,
            _codec.EncodePayload(new WireError(WireErrorCodes.RateLimited, null, 1500)));

        GoldenFile.Check("error-rate-limited.json", EncodeText(err));
    }

    [Fact]
    public void Frame_without_payload_matches_golden_bytes()
    {
        var ping = new Envelope(1, WireTypes.Ping, EnvelopeKind.Request, "p1", null, null, null, null, default);

        GoldenFile.Check("ping-no-payload.json", EncodeText(ping));
    }

    [Fact]
    public void Envelope_round_trips_every_field()
    {
        var e = Event();
        var w = new ArrayBufferWriter<byte>();
        _codec.Encode(e, w);

        var back = _codec.Decode(w.WrittenSpan);

        Assert.Equal((e.V, e.T, e.K, e.Id, e.Re, e.S, e.N, e.Ts), (back.V, back.T, back.K, back.Id, back.Re, back.S, back.N, back.Ts));
        Assert.Equal(e.P.ToArray(), back.P.ToArray());
    }

    [Fact]
    public void Payload_round_trips_through_the_envelope()
    {
        var msg = _codec.DecodePayload<WireGcMessage>(Decode(EncodeText(Event())));

        Assert.Equal(12, msg.N);
        Assert.Equal(40, msg.Ev);
        Assert.Equal("héllo \"world\"", msg.Text);
    }

    [Fact]
    public void Unknown_envelope_and_payload_fields_are_ignored()
    {
        var e = Decode("""
            {"v":1,"t":"lagged","k":"evt","future":{"a":[1,2,{"b":null}]},"s":"gc:x","p":{"s":"gc:x","lastSent":9,"extra":[true]},"zz":1}
            """);

        Assert.Equal("gc:x", e.S);
        Assert.Equal(new Lagged("gc:x", 9), _codec.DecodePayload<Lagged>(e));
    }

    [Fact]
    public void Unknown_message_type_decodes_with_the_payload_left_opaque()
    {
        var e = Decode("""{"v":1,"t":"brand.new","k":"req","id":"r1","p":{"anything":[1,2,3]}}""");

        Assert.Equal("brand.new", e.T);
        Assert.Equal("""{"anything":[1,2,3]}""", Encoding.UTF8.GetString(e.P.Span));
    }

    [Fact]
    public void Old_payload_read_by_a_new_decoder_leaves_added_fields_null()
    {
        var e = Decode("""{"v":1,"t":"gc.send","k":"req","p":{"gcId":"0196a6f0-0000-7000-8000-000000000001","text":"x"}}""");

        var send = _codec.DecodePayload<GcSend>(e);

        Assert.Null(send.Mentions);
        Assert.Null(send.ReplyToN);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"t":"x","k":"req"}""")]
    [InlineData("""{"v":1,"k":"req"}""")]
    [InlineData("""{"v":1,"t":"x"}""")]
    [InlineData("""{"v":1,"t":"x","k":"bogus"}""")]
    [InlineData("""{"v":"one","t":"x","k":"req"}""")]
    [InlineData("""{"v":1,"t":"x","k":"req","ts":"yesterday"}""")]
    [InlineData("""{"v":1,"t":"x","k":"req""")]
    public void Malformed_frames_throw_wire_format_exception(string frame)
    {
        Assert.Throws<WireFormatException>(() => _codec.Decode(Encoding.UTF8.GetBytes(frame)));
    }

    [Theory]
    [InlineData("""{"v":1,"t":"lagged","k":"evt"}""")]
    [InlineData("""{"v":1,"t":"lagged","k":"evt","p":null}""")]
    [InlineData("""{"v":1,"t":"lagged","k":"evt","p":{"s":5}}""")]
    public void Missing_or_mismatched_payload_throws_wire_format_exception(string frame)
    {
        var e = Decode(frame);

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Lagged>(e));
    }

    [Fact]
    public void Unregistered_payload_type_is_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => _codec.EncodePayload(new Unregistered(1)));
    }

    [Fact]
    public void Null_envelope_fields_decode_as_absent()
    {
        var e = Decode("""{"v":1,"t":"x","k":"res","id":null,"re":"r1","s":null,"n":null,"ts":null}""");

        Assert.Null(e.Id);
        Assert.Equal("r1", e.Re);
        Assert.Null(e.N);
        Assert.Null(e.Ts);
    }

    [Fact]
    public void Non_utc_timestamps_are_written_in_utc()
    {
        var e = new Envelope(1, "x", EnvelopeKind.Event, null, null, null, null, new DateTimeOffset(2026, 10, 25, 7, 36, 0, TimeSpan.FromHours(2)), default);

        Assert.Contains("\"ts\":\"2026-10-25T05:36:00+00:00\"", EncodeText(e));
    }

    private sealed record Unregistered(int X);
}
