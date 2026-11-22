using System.Buffers;
using System.Reflection;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Protobuf.Generated;
using Google.Protobuf;

namespace Bazaar.Wire.Codec.Protobuf.Tests;

/// <summary>Behaviour that is specific to the Protobuf shape: schema, envelope strictness, conventions, registry.</summary>
public class ProtobufWireCodecTests
{
    private static readonly ProtobufWireCodec _codec = new();
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    private static byte[] Frame(Envelope envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Encode(envelope, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static Envelope Wrap(string type, ReadOnlyMemory<byte> payload) =>
        new(1, type, EnvelopeKind.Request, "id-1", null, null, null, null, payload);

    [Fact]
    public void Registered_payloads_are_exactly_the_wire_payload_records_in_contracts()
    {
        var marked = typeof(WirePayloadAttribute).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<WirePayloadAttribute>().Any())
            .ToDictionary(t => t, t => t.GetCustomAttributes<WirePayloadAttribute>().Select(a => a.MessageType).Where(m => m is not null).Cast<string>().Order().ToArray());

        var registered = ProtobufWireCodec.RegisteredPayloads.ToDictionary(r => r.Type, r => r.MessageTypes.Order().ToArray());

        Assert.Equal(marked.Keys.Select(t => t.FullName).Order(), registered.Keys.Select(t => t.FullName).Order());
        foreach (var (type, messageTypes) in marked) Assert.Equal(messageTypes, registered[type]);
    }

    [Fact]
    public void Compiled_schema_text_equals_the_checked_in_wire_proto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var file = File.ReadAllText(Path.Combine(dir.FullName, "docs", "wire", "wire.proto")).Replace("\r\n", "\n");

        Assert.Equal(WireProtoSchema.Text, file);
    }

    [Fact]
    public void Schema_never_uses_any_extensions_or_groups()
    {
        Assert.DoesNotContain("google.protobuf", WireProtoSchema.Text);
        Assert.DoesNotContain("import ", WireProtoSchema.Text);
        Assert.DoesNotContain("extend ", WireProtoSchema.Text);
        Assert.DoesNotContain("group ", WireProtoSchema.Text);
    }

    [Fact]
    public void An_empty_payload_is_the_marker_that_readers_skip()
    {
        var payload = _codec.EncodePayload(new Ping(null));

        Assert.Equal(new byte[] { 0xF8, 0xFF, 0xFF, 0xFF, 0x0F, 0x00 }, payload.ToArray());
        Assert.Null(_codec.DecodePayload<Ping>(Wrap("ping", payload)).Nonce);
    }

    [Fact]
    public void A_payload_is_a_plain_protobuf_message_of_the_generated_schema()
    {
        var payload = _codec.EncodePayload(new GcRead(_gc, 12));

        var message = Proto.GcRead.Parser.ParseFrom(payload.ToArray());

        Assert.Equal(12, message.UpToN);
        Assert.Equal(16, message.GcId.Length);
    }

    [Fact]
    public void Guid_is_sixteen_bytes_in_rfc_4122_order()
    {
        var payload = _codec.EncodePayload(new GcRead(_gc, 1));

        var message = Proto.GcRead.Parser.ParseFrom(payload.ToArray());

        Assert.Equal(Convert.FromHexString("0196a6f0000070008000000000000001"), message.GcId.ToByteArray());
    }

    [Fact]
    public void A_frame_parses_as_the_generated_envelope_message()
    {
        var frame = Frame(new Envelope(1, "ping", EnvelopeKind.Request, "id-1", null, "gc:x", 5, new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.FromHours(2)), _codec.EncodePayload(new Ping("n"))));

        var message = Proto.Envelope.Parser.ParseFrom(frame);

        Assert.Equal("ping", message.T);
        Assert.Equal("id-1", message.Id);
        Assert.False(message.HasRe);
        Assert.Equal(120, message.Ts.OffsetMinutes);
        Assert.True(message.End);
    }

    [Fact]
    public void Time_offset_survives_the_payload()
    {
        var local = new DateTimeOffset(2026, 11, 19, 7, 36, 12, TimeSpan.FromHours(2));
        var sent = new Welcome(1, 0, "protobuf", _gc, local, 25, 60, new WireLimits(1, 2, 3), [], 0, null, null);

        var received = _codec.DecodePayload<Welcome>(Wrap("welcome", _codec.EncodePayload(sent)));

        Assert.Equal(local, received.ServerTime);
        Assert.Equal(TimeSpan.FromHours(2), received.ServerTime.Offset);
    }

    [Fact]
    public void Two_frames_glued_together_are_rejected_not_merged()
    {
        var frame = Frame(Wrap("ping", _codec.EncodePayload(new Ping(null))));

        Assert.Throws<WireFormatException>(() => _codec.Decode([.. frame, .. frame]));
    }

    [Fact]
    public void A_frame_without_the_end_marker_is_rejected()
    {
        var message = new Proto.Envelope { V = 1, T = "ping", K = "request" };

        Assert.Throws<WireFormatException>(() => _codec.Decode(message.ToByteArray()));
    }

    [Fact]
    public void A_known_envelope_field_with_the_wrong_wire_type_is_rejected()
    {
        // Field 2 (t) as a varint instead of a string.
        byte[] frame = [0x08, 0x01, 0x10, 0x01, 0x50, 0x01];

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_unknown_envelope_field_may_sit_anywhere()
    {
        var frame = Frame(Wrap("ping", _codec.EncodePayload(new Ping("n"))));
        // Field 99, varint 7, after the version field.
        byte[] injected = [.. frame.AsSpan(0, 2), .. ProtobufTestData.Tag(99, 0), 0x07, .. frame.AsSpan(2)];

        var decoded = _codec.Decode(injected);

        Assert.Equal("ping", decoded.T);
    }

    [Fact]
    public void A_byte_field_out_of_range_is_rejected()
    {
        // Direct runtime check: the narrowing helper refuses anything above 255.
        Assert.Throws<WireFormatException>(() => ProtobufWireRuntime.Byte(256));
        Assert.Equal((byte)255, ProtobufWireRuntime.Byte(255));
    }

    [Fact]
    public void A_guid_of_the_wrong_length_is_rejected()
    {
        var message = new Proto.GcRead { GcId = ByteString.CopyFrom(1, 2, 3), UpToN = 1 };

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcRead>(Wrap("gc.read", message.ToByteArray())));
    }

    [Fact]
    public void A_missing_nested_message_is_rejected()
    {
        // Welcome.limits is a required nested record; a payload without it is not a Welcome.
        var message = new Proto.Welcome();

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Welcome>(Wrap("welcome", message.ToByteArray())));
    }

    [Fact]
    public void An_unknown_enum_value_is_rejected_when_the_enum_has_no_fallback()
    {
        var message = new Proto.GcMemberAdd { GcId = ByteString.CopyFrom(new byte[16]), PrincipalId = ByteString.CopyFrom(new byte[16]), Kind = (Proto.PrincipalKind)99, Role = "member" };

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcMemberAdd>(Wrap("gc.member.add", message.ToByteArray())));
    }

    [Fact]
    public void An_unknown_enum_value_falls_back_when_the_enum_has_an_unspecified_member()
    {
        var message = new Proto.GraphSnapshot();
        message.Edges.Add(new Proto.GraphEdge { From = "a", To = "b", Kind = (Proto.GraphEdgeKind)99 });

        var decoded = _codec.DecodePayload<Bazaar.Contracts.Graph.GraphSnapshot>(Wrap("sample", message.ToByteArray()));

        Assert.Equal(Bazaar.Contracts.Graph.GraphEdgeKind.Unspecified, decoded.Edges[0].Kind);
    }

    [Fact]
    public void A_nullable_list_keeps_empty_apart_from_absent()
    {
        var empty = _codec.DecodePayload<GcSend>(Wrap("gc.send", _codec.EncodePayload(new GcSend(_gc, "hi", [], null))));
        var absent = _codec.DecodePayload<GcSend>(Wrap("gc.send", _codec.EncodePayload(new GcSend(_gc, "hi", null, null))));

        Assert.NotNull(empty.Mentions);
        Assert.Empty(empty.Mentions);
        Assert.Null(absent.Mentions);
    }

    [Fact]
    public void Encoding_the_same_value_twice_gives_the_same_bytes()
    {
        var value = new GcSend(_gc, "hi", [_gc, _gc], 4);

        Assert.True(_codec.EncodePayload(value).Span.SequenceEqual(_codec.EncodePayload(value).Span));
    }
}
