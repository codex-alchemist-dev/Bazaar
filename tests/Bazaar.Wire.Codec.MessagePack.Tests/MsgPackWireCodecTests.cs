using System.Buffers;
using System.Reflection;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;
using global::MessagePack;
using Xunit;

namespace Bazaar.Wire.Codec.MessagePack.Tests;

/// <summary>Behaviour that is specific to the MessagePack shape: map keys, types, enums, strictness, registry.</summary>
public class MsgPackWireCodecTests
{
    private delegate void Build(ref MessagePackWriter w);

    private static readonly MsgPackWireCodec _codec = new();
    private static readonly string[] _envelopeKeys = ["v", "t", "k", "id", "re", "s", "n", "ts", "p"];
    private static readonly string[] _requiredKeys = ["v", "t", "k"];
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    public static TheoryData<string> SampleNames => PayloadSamples.Names;

    private static Envelope Wrap(string type, ReadOnlyMemory<byte> payload) =>
        new(1, type, EnvelopeKind.Request, "id-1", null, null, null, null, payload);

    private static byte[] Write(Build build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        build(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Frame(Envelope envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Encode(envelope, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    [Fact]
    public void Registered_payloads_are_exactly_the_wire_payload_records_in_contracts()
    {
        var marked = typeof(WirePayloadAttribute).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<WirePayloadAttribute>().Any())
            .ToDictionary(t => t, t => t.GetCustomAttributes<WirePayloadAttribute>().Select(a => a.MessageType).Where(m => m is not null).Cast<string>().Order().ToArray());

        var registered = MsgPackWireCodec.RegisteredPayloads.ToDictionary(r => r.Type, r => r.MessageTypes.Order().ToArray());

        var byName = Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        Assert.Equal(marked.Keys.Order(byName), registered.Keys.Order(byName));
        foreach (var (type, messageTypes) in marked) Assert.Equal(messageTypes, registered[type]);
    }

    [Fact]
    public void Payload_with_only_null_fields_is_an_empty_map()
    {
        Assert.Equal(new byte[] { 0x80 }, _codec.EncodePayload(new Ping(null)).ToArray());
    }

    [Fact]
    public void Payload_map_has_camel_case_text_keys_and_omits_nulls()
    {
        // {"nonce": "n"}
        Assert.Equal(new byte[] { 0x81, 0xA5, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0xA1, (byte)'n' }, _codec.EncodePayload(new Ping("n")).ToArray());
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Map_keys_equal_the_json_property_names(string name)
    {
        var sample = PayloadSamples.Get(name);
        var bytes = sample.Encode(_codec);
        var reader = new MessagePackReader(bytes);
        using var json = JsonDocument.Parse(sample.CanonicalJson());

        AssertSameShape(json.RootElement, ref reader, name);
        Assert.True(reader.End);
    }

    [Fact]
    public void Envelope_is_a_map_with_the_doc_02_field_names()
    {
        var frame = Frame(new Envelope(1, "gc.send", EnvelopeKind.Request, "id-1", "re-1", "gc:x", 5, new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), _codec.EncodePayload(new Ping(null))));
        var reader = new MessagePackReader(frame);
        var keys = new List<string>();

        var count = reader.ReadMapHeader();
        for (var i = 0; i < count; i++)
        {
            keys.Add(reader.ReadString()!);
            reader.Skip();
        }

        Assert.Equal(_envelopeKeys, keys);
    }

    [Fact]
    public void Envelope_time_is_the_messagepack_timestamp_extension()
    {
        var frame = Frame(new Envelope(1, "ping", EnvelopeKind.Event, null, null, null, null, new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.FromHours(2)), default));
        var reader = new MessagePackReader(frame);
        var count = reader.ReadMapHeader();
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadString() != "ts") { reader.Skip(); continue; }
            Assert.Equal(MessagePackType.Extension, reader.NextMessagePackType);
            var header = reader.ReadExtensionFormatHeader();
            Assert.Equal(ReservedMessagePackExtensionTypeCode.DateTime, header.TypeCode);
            return;
        }

        Assert.Fail("No ts in the envelope.");
    }

    [Fact]
    public void Envelope_payload_is_embedded_as_a_native_item_not_as_a_binary()
    {
        var payload = _codec.EncodePayload(new Ping("n"));
        var frame = Frame(Wrap("ping", payload));
        var reader = new MessagePackReader(frame);
        var count = reader.ReadMapHeader();
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadString() != "p") { reader.Skip(); continue; }
            Assert.Equal(MessagePackType.Map, reader.NextMessagePackType);
            return;
        }

        Assert.Fail("No p in the envelope.");
    }

    [Fact]
    public void An_unknown_enum_value_maps_to_the_unknown_member_when_one_exists()
    {
        var future = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(3);
            w.Write("graphVersion");
            w.Write(1);
            w.Write("nodes");
            w.WriteArrayHeader(2);
            w.WriteMapHeader(2);
            w.Write("id");
            w.Write("a");
            w.Write("kind");
            w.Write("phase");
            w.WriteMapHeader(2);
            w.Write("id");
            w.Write("b");
            w.Write("kind");
            w.Write("someFutureKind");
            w.Write("edges");
            w.WriteArrayHeader(0);
        });

        var snapshot = _codec.DecodePayload<GraphSnapshot>(Wrap("x", future));

        Assert.Equal(GraphNodeKind.Phase, snapshot.Nodes[0].Kind);
        Assert.Equal(GraphNodeKind.Unspecified, snapshot.Nodes[1].Kind);
    }

    [Fact]
    public void Enums_use_the_json_member_names()
    {
        var payload = _codec.EncodePayload(new GcMemberAdd(_gc, _gc, PrincipalKind.Agent, "member"));
        var reader = new MessagePackReader(payload);
        var count = reader.ReadMapHeader();
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadString() != "kind") { reader.Skip(); continue; }
            Assert.Equal("agent", reader.ReadString());
            return;
        }

        Assert.Fail("No kind in the payload.");
    }

    [Fact]
    public void An_unknown_enum_value_without_an_unknown_member_is_a_wire_format_error()
    {
        var future = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(4);
            w.Write("gcId");
            w.Write(_gc.ToString("D"));
            w.Write("principalId");
            w.Write(_gc.ToString("D"));
            w.Write("kind");
            w.Write("robot");
            w.Write("role");
            w.Write("member");
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcMemberAdd>(Wrap("gc.member.add", future)));
    }

    [Fact]
    public void A_missing_required_field_is_a_wire_format_error()
    {
        var withoutText = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write("gcId");
            w.Write(_gc.ToString("D"));
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcSend>(Wrap("gc.send", withoutText)));
    }

    [Fact]
    public void A_field_of_the_wrong_type_is_a_wire_format_error()
    {
        var wrong = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(2);
            w.Write("gcId");
            w.Write(5);
            w.Write("text");
            w.Write("x");
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcSend>(Wrap("gc.send", wrong)));
    }

    [Fact]
    public void An_integer_that_overflows_its_field_is_a_wire_format_error()
    {
        var wrong = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(2);
            w.Write("gcId");
            w.Write(_gc.ToString("D"));
            w.Write("limit");
            w.Write(long.MaxValue);
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcHistory>(Wrap("gc.history", wrong)));
    }

    [Fact]
    public void A_record_constructor_that_refuses_the_values_is_a_wire_format_error()
    {
        // Dimensions 5: TileFrame only allows 2 or 3.
        var bad = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(9);
            w.Write("viewId");
            w.Write("v");
            w.Write("graphVersion");
            w.Write(1);
            w.Write("layoutSeq");
            w.Write(1);
            w.Write("key");
            w.WriteMapHeader(4);
            w.Write("level");
            w.Write(0);
            w.Write("x");
            w.Write(0);
            w.Write("y");
            w.Write(0);
            w.Write("z");
            w.Write(0);
            w.Write("tier");
            w.Write("far");
            w.Write("dimensions");
            w.Write(5);
            w.Write("nodeIds");
            w.WriteArrayHeader(0);
            w.Write("xyz");
            w.Write(ReadOnlySpan<byte>.Empty);
            w.Write("clusters");
            w.WriteArrayHeader(0);
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<TileFrame>(Wrap("x", bad)));
    }

    [Fact]
    public void Duplicate_map_keys_are_rejected()
    {
        // map(2) { "nonce": "a", "nonce": "b" }
        byte[] duplicate =
        [
            0x82, 0xA5, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0xA1, (byte)'a',
            0xA5, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0xA1, (byte)'b',
        ];

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", duplicate)));
    }

    [Fact]
    public void Duplicate_envelope_keys_are_rejected()
    {
        byte[] duplicate = [0x82, 0xA1, (byte)'v', 0x01, 0xA1, (byte)'v', 0x01];

        Assert.Throws<WireFormatException>(() => _codec.Decode(duplicate));
    }

    [Fact]
    public void A_non_text_map_key_is_rejected()
    {
        var intKey = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(1);
            w.Write(1);
            w.Write("a");
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", intKey)));
    }

    [Fact]
    public void Invalid_utf8_in_a_string_is_rejected()
    {
        // map(1) { "nonce": str(1) 0xFF }
        byte[] bad = [0x81, 0xA5, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0xA1, 0xFF];

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", bad)));
    }

    [Fact]
    public void A_payload_that_is_not_a_map_is_rejected()
    {
        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", new byte[] { 0x01 })));
        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", new byte[] { 0xC0 })));
    }

    [Fact]
    public void The_never_used_type_code_is_rejected_inside_an_unknown_field()
    {
        var frame = MsgPackTestData.AddField(Frame(Wrap("ping", _codec.EncodePayload(new Ping(null)))), "x-future", [0xC1]);

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void A_binary_item_over_the_string_cap_is_rejected_inside_an_unknown_field()
    {
        var codec = new MsgPackWireCodec(new WireDecodeLimits(MaxStringBytes: 16));
        var frame = MsgPackTestData.AddField(Frame(Wrap("ping", _codec.EncodePayload(new Ping(null)))), "x-future", [0xC4, 17, .. new byte[17]]);

        Assert.Throws<WireFormatException>(() => codec.Decode(frame));
    }

    [Fact]
    public void A_huge_declared_length_without_the_bytes_is_rejected_without_allocating()
    {
        // array32 claiming 4 billion items, then nothing.
        byte[] bomb = [0xDD, 0xFF, 0xFF, 0xFF, 0xFF];
        var frame = MsgPackTestData.AddField(Frame(Wrap("ping", _codec.EncodePayload(new Ping(null)))), "x-future", bomb);

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Theory]
    [InlineData("v")]
    [InlineData("t")]
    [InlineData("k")]
    public void An_envelope_without_a_required_field_is_rejected(string missing)
    {
        var frame = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(2);
            foreach (var key in _requiredKeys.Where(k => k != missing))
            {
                w.Write(key);
                if (key == "v") w.Write(1); else w.Write(key == "k" ? "req" : "ping");
            }
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_with_an_unknown_kind_is_rejected()
    {
        var frame = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(3);
            w.Write("v");
            w.Write(1);
            w.Write("t");
            w.Write("ping");
            w.Write("k");
            w.Write("later");
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_with_an_oversized_field_is_rejected_by_the_string_cap()
    {
        var frame = Write((ref MessagePackWriter w) =>
        {
            w.WriteMapHeader(3);
            w.Write("v");
            w.Write(1);
            w.Write("t");
            w.Write(new string('a', WireDecodeLimits.DefaultMaxStringBytes + 1));
            w.Write("k");
            w.Write("req");
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void Messagepack_is_smaller_than_json_for_the_same_replay_page()
    {
        var sample = PayloadSamples.Get("Resync");
        var json = new JsonWireCodec();

        Assert.True(sample.Encode(_codec).Length < sample.Encode(json).Length);
    }

    private static void AssertSameShape(JsonElement json, ref MessagePackReader reader, string path)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    Assert.Equal(MessagePackType.Map, reader.NextMessagePackType);
                    var count = reader.ReadMapHeader();
                    var keys = new List<string>();
                    var jsonProperties = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                    for (var i = 0; i < count; i++)
                    {
                        var key = reader.ReadString()!;
                        keys.Add(key);
                        Assert.True(jsonProperties.TryGetValue(key, out var value), $"{path}: MessagePack key '{key}' has no JSON property.");
                        AssertSameShape(value, ref reader, path + "." + key);
                    }

                    Assert.Equal(jsonProperties.Keys.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
                    break;
                }

            case JsonValueKind.Array:
                {
                    if (reader.NextMessagePackType == MessagePackType.Binary)
                    {
                        // A float array: one bin standing for the JSON number array.
                        Assert.Equal(json.GetArrayLength() * sizeof(float), reader.ReadBytes()!.Value.Length);
                        break;
                    }

                    var count = reader.ReadArrayHeader();
                    Assert.Equal(json.GetArrayLength(), count);
                    foreach (var item in json.EnumerateArray()) AssertSameShape(item, ref reader, path + "[]");
                    break;
                }

            default:
                reader.Skip();
                break;
        }
    }
}
