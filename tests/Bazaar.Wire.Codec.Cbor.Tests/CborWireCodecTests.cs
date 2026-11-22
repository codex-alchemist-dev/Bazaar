using System.Buffers;
using System.Formats.Cbor;
using System.Reflection;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;

namespace Bazaar.Wire.Codec.Cbor.Tests;

/// <summary>Behaviour that is specific to the CBOR shape: map keys, tags, enums, strictness, registry.</summary>
public class CborWireCodecTests
{
    private static readonly CborWireCodec _codec = new();
    private static readonly string[] _envelopeKeys = ["v", "t", "k", "id", "re", "s", "n", "ts", "p"];
    private static readonly string[] _requiredKeys = ["v", "t", "k"];
    private static readonly float[] _twoFloats = [1f, 2f];
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    public static TheoryData<string> SampleNames => PayloadSamples.Names;

    private static Envelope Wrap(string type, ReadOnlyMemory<byte> payload) =>
        new(1, type, EnvelopeKind.Request, "id-1", null, null, null, null, payload);

    private static byte[] Write(Action<CborWriter> build)
    {
        var writer = new CborWriter(CborConformanceMode.Strict);
        build(writer);
        return writer.Encode();
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

        var registered = CborWireCodec.RegisteredPayloads.ToDictionary(r => r.Type, r => r.MessageTypes.Order().ToArray());

        Assert.Equal(marked.Keys.Order(Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName))), registered.Keys.Order(Comparer<Type>.Create((a, b) => string.CompareOrdinal(a.FullName, b.FullName))));
        foreach (var (type, messageTypes) in marked) Assert.Equal(messageTypes, registered[type]);
    }

    [Fact]
    public void Payload_with_only_null_fields_is_an_empty_map()
    {
        Assert.Equal(new byte[] { 0xA0 }, _codec.EncodePayload(new Ping(null)).ToArray());
    }

    [Fact]
    public void Payload_map_has_camel_case_text_keys_and_omits_nulls()
    {
        // {"nonce": "n"}
        Assert.Equal(new byte[] { 0xA1, 0x65, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0x61, (byte)'n' }, _codec.EncodePayload(new Ping("n")).ToArray());
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Map_keys_equal_the_json_property_names(string name)
    {
        var sample = PayloadSamples.Get(name);
        var cbor = new CborReader(sample.Encode(_codec), CborConformanceMode.Strict);
        using var json = JsonDocument.Parse(sample.CanonicalJson());

        AssertSameShape(json.RootElement, cbor, name);
        Assert.Equal(CborReaderState.Finished, cbor.PeekState());
    }

    [Fact]
    public void Envelope_is_a_map_with_the_doc_02_field_names()
    {
        var frame = Frame(new Envelope(1, "gc.send", EnvelopeKind.Request, "id-1", "re-1", "gc:x", 5, new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), _codec.EncodePayload(new Ping(null))));
        var reader = new CborReader(frame, CborConformanceMode.Strict);
        var keys = new List<string>();

        var count = reader.ReadStartMap();
        for (var i = 0; i < count; i++)
        {
            keys.Add(reader.ReadTextString());
            reader.SkipValue();
        }

        Assert.Equal(_envelopeKeys, keys);
    }

    [Fact]
    public void Envelope_payload_is_embedded_as_a_native_item_not_as_a_byte_string()
    {
        var frame = Frame(Wrap("ping", _codec.EncodePayload(new Ping("n"))));
        var reader = new CborReader(frame, CborConformanceMode.Strict);
        reader.ReadStartMap();
        while (reader.ReadTextString() is var key && key != "p") reader.SkipValue();

        Assert.Equal(CborReaderState.StartMap, reader.PeekState());
    }

    [Fact]
    public void Float_arrays_use_the_little_endian_binary32_typed_array_tag()
    {
        var payload = _codec.EncodePayload(new LayoutFrame(1, 2, "f", _twoFloats));
        var reader = new CborReader(payload, CborConformanceMode.Strict);
        reader.ReadStartMap();
        while (reader.ReadTextString() != "xyz") reader.SkipValue();

        Assert.Equal((CborTag)85, reader.ReadTag());
        Assert.Equal(new byte[] { 0, 0, 0x80, 0x3F, 0, 0, 0, 0x40 }, reader.ReadByteString());
    }

    [Fact]
    public void An_unknown_enum_value_maps_to_the_unknown_member_when_one_exists()
    {
        var future = Write(w =>
        {
            w.WriteStartMap(3);
            w.WriteTextString("graphVersion");
            w.WriteInt32(1);
            w.WriteTextString("nodes");
            w.WriteStartArray(2);
            w.WriteStartMap(2);
            w.WriteTextString("id");
            w.WriteTextString("a");
            w.WriteTextString("kind");
            w.WriteTextString("phase");
            w.WriteEndMap();
            w.WriteStartMap(2);
            w.WriteTextString("id");
            w.WriteTextString("b");
            w.WriteTextString("kind");
            w.WriteTextString("someFutureKind");
            w.WriteEndMap();
            w.WriteEndArray();
            w.WriteTextString("edges");
            w.WriteStartArray(0);
            w.WriteEndArray();
            w.WriteEndMap();
        });

        var snapshot = _codec.DecodePayload<GraphSnapshot>(Wrap("x", future));

        Assert.Equal(GraphNodeKind.Phase, snapshot.Nodes[0].Kind);
        Assert.Equal(GraphNodeKind.Unspecified, snapshot.Nodes[1].Kind);
    }

    [Fact]
    public void Enums_use_the_json_member_names()
    {
        var payload = _codec.EncodePayload(new GcMemberAdd(_gc, _gc, PrincipalKind.Agent, "member"));
        var reader = new CborReader(payload, CborConformanceMode.Strict);
        reader.ReadStartMap();
        while (reader.ReadTextString() != "kind") reader.SkipValue();

        Assert.Equal("agent", reader.ReadTextString());
    }

    [Fact]
    public void An_unknown_enum_value_without_an_unknown_member_is_a_wire_format_error()
    {
        var future = Write(w =>
        {
            w.WriteStartMap(4);
            w.WriteTextString("gcId");
            w.WriteTextString(_gc.ToString("D"));
            w.WriteTextString("principalId");
            w.WriteTextString(_gc.ToString("D"));
            w.WriteTextString("kind");
            w.WriteTextString("robot");
            w.WriteTextString("role");
            w.WriteTextString("member");
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcMemberAdd>(Wrap("gc.member.add", future)));
    }

    [Fact]
    public void A_missing_required_field_is_a_wire_format_error()
    {
        var withoutText = Write(w =>
        {
            w.WriteStartMap(1);
            w.WriteTextString("gcId");
            w.WriteTextString(_gc.ToString("D"));
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcSend>(Wrap("gc.send", withoutText)));
    }

    [Fact]
    public void A_field_of_the_wrong_type_is_a_wire_format_error()
    {
        var wrong = Write(w =>
        {
            w.WriteStartMap(2);
            w.WriteTextString("gcId");
            w.WriteInt32(5);
            w.WriteTextString("text");
            w.WriteTextString("x");
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcSend>(Wrap("gc.send", wrong)));
    }

    [Fact]
    public void An_integer_that_overflows_its_field_is_a_wire_format_error()
    {
        var wrong = Write(w =>
        {
            w.WriteStartMap(2);
            w.WriteTextString("gcId");
            w.WriteTextString(_gc.ToString("D"));
            w.WriteTextString("limit");
            w.WriteInt64(long.MaxValue);
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcHistory>(Wrap("gc.history", wrong)));
    }

    [Fact]
    public void A_record_constructor_that_refuses_the_values_is_a_wire_format_error()
    {
        // Dimensions 5: TileFrame only allows 2 or 3.
        var bad = Write(w =>
        {
            w.WriteStartMap(9);
            w.WriteTextString("viewId");
            w.WriteTextString("v");
            w.WriteTextString("graphVersion");
            w.WriteInt32(1);
            w.WriteTextString("layoutSeq");
            w.WriteInt32(1);
            w.WriteTextString("key");
            w.WriteStartMap(4);
            w.WriteTextString("level");
            w.WriteInt32(0);
            w.WriteTextString("x");
            w.WriteInt32(0);
            w.WriteTextString("y");
            w.WriteInt32(0);
            w.WriteTextString("z");
            w.WriteInt32(0);
            w.WriteEndMap();
            w.WriteTextString("tier");
            w.WriteTextString("far");
            w.WriteTextString("dimensions");
            w.WriteInt32(5);
            w.WriteTextString("nodeIds");
            w.WriteStartArray(0);
            w.WriteEndArray();
            w.WriteTextString("xyz");
            w.WriteTag((CborTag)85);
            w.WriteByteString([]);
            w.WriteTextString("clusters");
            w.WriteStartArray(0);
            w.WriteEndArray();
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<TileFrame>(Wrap("x", bad)));
    }

    [Fact]
    public void Duplicate_map_keys_are_rejected()
    {
        // map(2) { "nonce": "a", "nonce": "b" }, written by hand because the writer refuses duplicates.
        byte[] duplicate =
        [
            0xA2, 0x65, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0x61, (byte)'a',
            0x65, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0x61, (byte)'b',
        ];

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", duplicate)));
    }

    [Fact]
    public void A_non_text_map_key_is_rejected()
    {
        var intKey = Write(w =>
        {
            w.WriteStartMap(1);
            w.WriteInt32(1);
            w.WriteTextString("a");
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", intKey)));
    }

    [Fact]
    public void Invalid_utf8_in_a_string_is_rejected()
    {
        // map(1) { "nonce": text(1) 0xFF }
        byte[] bad = [0xA1, 0x65, (byte)'n', (byte)'o', (byte)'n', (byte)'c', (byte)'e', 0x61, 0xFF];

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", bad)));
    }

    [Fact]
    public void A_payload_that_is_not_a_map_is_rejected()
    {
        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", new byte[] { 0x01 })));
        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", new byte[] { 0xF6 })));
    }

    [Fact]
    public void A_chain_of_tags_counts_toward_the_depth_cap()
    {
        var tags = new byte[200];
        Array.Fill(tags, (byte)0xC1);
        tags[^1] = 0x00;
        var payload = _codec.EncodePayload(new Ping(null));
        var frame = CborTestData.AddField(Frame(Wrap("ping", payload)), "x-future", tags);

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Theory]
    [InlineData("v")]
    [InlineData("t")]
    [InlineData("k")]
    public void An_envelope_without_a_required_field_is_rejected(string missing)
    {
        var frame = Write(w =>
        {
            w.WriteStartMap(2);
            foreach (var key in _requiredKeys.Where(k => k != missing))
            {
                w.WriteTextString(key);
                if (key == "v") w.WriteInt32(1); else w.WriteTextString(key == "k" ? "req" : "ping");
            }

            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_with_an_unknown_kind_is_rejected()
    {
        var frame = Write(w =>
        {
            w.WriteStartMap(3);
            w.WriteTextString("v");
            w.WriteInt32(1);
            w.WriteTextString("t");
            w.WriteTextString("ping");
            w.WriteTextString("k");
            w.WriteTextString("later");
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_with_an_oversized_field_is_rejected_by_the_string_cap()
    {
        var frame = Write(w =>
        {
            w.WriteStartMap(3);
            w.WriteTextString("v");
            w.WriteInt32(1);
            w.WriteTextString("t");
            w.WriteTextString(new string('a', WireDecodeLimits.DefaultMaxStringBytes + 1));
            w.WriteTextString("k");
            w.WriteTextString("req");
            w.WriteEndMap();
        });

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void Cbor_is_smaller_than_json_for_the_same_replay_page()
    {
        var sample = PayloadSamples.Get("Resync");
        var json = new JsonWireCodec();

        Assert.True(sample.Encode(_codec).Length < sample.Encode(json).Length);
    }

    private static void AssertSameShape(JsonElement json, CborReader cbor, string path)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    Assert.Equal(CborReaderState.StartMap, cbor.PeekState());
                    var count = cbor.ReadStartMap() ?? throw new InvalidOperationException();
                    var cborKeys = new List<string>();
                    var jsonProperties = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                    for (var i = 0; i < count; i++)
                    {
                        var key = cbor.ReadTextString();
                        cborKeys.Add(key);
                        Assert.True(jsonProperties.TryGetValue(key, out var value), $"{path}: CBOR key '{key}' has no JSON property.");
                        AssertSameShape(value, cbor, path + "." + key);
                    }

                    cbor.ReadEndMap();
                    Assert.Equal(jsonProperties.Keys.Order(StringComparer.Ordinal), cborKeys.Order(StringComparer.Ordinal));
                    break;
                }

            case JsonValueKind.Array:
                {
                    if (cbor.PeekState() == CborReaderState.Tag)
                    {
                        // A float array: one tagged byte string standing for the JSON number array.
                        cbor.ReadTag();
                        Assert.Equal(json.GetArrayLength() * sizeof(float), cbor.ReadByteString().Length);
                        break;
                    }

                    var count = cbor.ReadStartArray() ?? throw new InvalidOperationException();
                    Assert.Equal(json.GetArrayLength(), count);
                    foreach (var item in json.EnumerateArray()) AssertSameShape(item, cbor, path + "[]");
                    cbor.ReadEndArray();
                    break;
                }

            default:
                cbor.SkipValue();
                break;
        }
    }
}
