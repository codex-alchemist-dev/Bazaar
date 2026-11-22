using System.Buffers;
using System.Reflection;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Conformance;
using MemoryPack;

namespace Bazaar.Wire.Codec.MemoryPack.Tests;

/// <summary>Behaviour that is specific to the MemoryPack shape: layout, hostile headers, enums, registry (doc 90 section 3.2).</summary>
public class MemoryPackWireCodecTests
{
    private static readonly MemoryPackWireCodec _codec = new();
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    private static Envelope Wrap(string type, ReadOnlyMemory<byte> payload) =>
        new(1, type, EnvelopeKind.Request, "id-1", null, null, null, null, payload);

    private static byte[] Frame(MemoryPackWireCodec codec, Envelope envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Encode(envelope, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>One version-tolerant object whose only member is <paramref name="member"/>.</summary>
    private static byte[] OneMemberObject(params byte[] member) => [1, (byte)member.Length, .. member];

    [Fact]
    public void Registered_payloads_are_exactly_the_wire_payload_records_in_contracts()
    {
        var marked = typeof(WirePayloadAttribute).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<WirePayloadAttribute>().Any())
            .ToDictionary(t => t, t => t.GetCustomAttributes<WirePayloadAttribute>().Select(a => a.MessageType).Where(m => m is not null).Cast<string>().Order().ToArray());

        var registered = MemoryPackWireCodec.RegisteredPayloads.ToDictionary(r => r.Type, r => r.MessageTypes.Order().ToArray());

        Assert.Equal(marked.Keys.Select(t => t.FullName).Order(StringComparer.Ordinal), registered.Keys.Select(t => t.FullName).Order(StringComparer.Ordinal));
        foreach (var (type, messageTypes) in marked) Assert.Equal(messageTypes, registered[type]);
    }

    [Fact]
    public void Contracts_does_not_reference_memorypack()
    {
        var references = typeof(WirePayloadAttribute).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name!.StartsWith("MemoryPack", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fully_populated_payload_has_the_same_bytes_as_memorypacks_own_version_tolerant_serializer()
    {
        var hello = new Hello(1, 2, 3, ["json", "memorypack"], "device", "windows", "0.1.0", "credential", [new StreamCursor("gc:a", 4, 5)]);
        var mirror = new HelloMirror
        {
            VersionMin = 1,
            VersionMax = 2,
            Minor = 3,
            Codecs = ["json", "memorypack"],
            DeviceId = "device",
            DeviceClass = "windows",
            ClientBuild = "0.1.0",
            Credential = "credential",
            Cursors = [new StreamCursorMirror { S = "gc:a", After = 4, EditAfter = 5 }],
        };

        Assert.Equal(MemoryPackSerializer.Serialize(mirror), _codec.EncodePayload(hello).ToArray());
    }

    [Fact]
    public void Data_written_by_memorypacks_own_serializer_decodes_even_when_null_members_are_spelled_out()
    {
        var mirror = new HelloMirror
        {
            VersionMin = 1,
            VersionMax = 1,
            Minor = 0,
            Codecs = ["json"],
            DeviceId = "d",
            DeviceClass = "c",
            ClientBuild = "b",
            Credential = null,
            Cursors = null,
        };

        var decoded = _codec.DecodePayload<Hello>(Wrap("hello", MemoryPackSerializer.Serialize(mirror)));

        Assert.Null(decoded.Credential);
        Assert.Null(decoded.Cursors);
        Assert.Equal("d", decoded.DeviceId);
    }

    [Fact]
    public void A_payload_from_an_older_sender_with_fewer_members_reads_the_rest_as_null()
    {
        var old = new HelloV0 { VersionMin = 1, VersionMax = 1, Minor = 0, Codecs = ["json"], DeviceId = "d", DeviceClass = "c", ClientBuild = "b" };

        var decoded = _codec.DecodePayload<Hello>(Wrap("hello", MemoryPackSerializer.Serialize(old)));

        Assert.Null(decoded.Credential);
        Assert.Null(decoded.Cursors);
        Assert.Equal(["json"], decoded.Codecs);
    }

    [Fact]
    public void A_payload_from_a_newer_sender_with_extra_members_is_read_and_the_extras_skipped()
    {
        var future = new HelloV2 { VersionMin = 1, VersionMax = 1, Minor = 0, Codecs = ["json"], DeviceId = "d", DeviceClass = "c", ClientBuild = "b", Credential = "k", Cursors = [], Future = "later", FutureNumber = 9 };

        var decoded = _codec.DecodePayload<Hello>(Wrap("hello", MemoryPackSerializer.Serialize(future)));

        Assert.Equal("k", decoded.Credential);
        Assert.Empty(decoded.Cursors!);
    }

    [Fact]
    public void Payload_with_only_null_members_is_a_header_and_zero_lengths()
    {
        // 1 member (nonce), zero bytes long.
        Assert.Equal(new byte[] { 1, 0 }, _codec.EncodePayload(new Ping(null)).ToArray());
    }

    [Fact]
    public void A_collection_header_that_claims_a_billion_items_is_rejected_before_anything_is_allocated()
    {
        var hostile = OneMemberObject(0x00, 0x00, 0x00, 0x40);

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Unsubscribe>(Wrap("unsubscribe", hostile)));
    }

    [Fact]
    public void A_negative_collection_header_is_rejected()
    {
        var hostile = OneMemberObject(0xFE, 0xFF, 0xFF, 0xFF);

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Unsubscribe>(Wrap("unsubscribe", hostile)));
    }

    [Fact]
    public void A_string_header_that_claims_two_gigabytes_is_rejected()
    {
        // Ping.Nonce: UTF-8 string with ~byteCount = int.MinValue + 1.
        var hostile = OneMemberObject(0x01, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00);

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", hostile)));
    }

    [Fact]
    public void A_member_that_does_not_use_the_bytes_its_length_announced_is_rejected()
    {
        var sample = _codec.EncodePayload(new Ping("nonce")).ToArray();
        // Lengthen the member by one byte without adding data: the frame then ends early.
        var longer = (byte[])sample.Clone();
        longer[1]++;

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", longer)));
    }

    [Fact]
    public void An_object_that_announces_more_member_bytes_than_exist_is_rejected()
    {
        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Ping>(Wrap("ping", new byte[] { 1, 100, 1, 2 })));
    }

    [Fact]
    public void A_flag_byte_other_than_0_or_1_is_rejected()
    {
        var payload = _codec.EncodePayload(new Welcome(1, 0, "memorypack", _gc, DateTimeOffset.UnixEpoch, 5, 15, new WireLimits(1, 2, 3), ["a"], 1, true, null)).ToArray();
        // UpgradeRequired is the 11th member and the last one with data: its single byte is the true flag, at the very end.
        payload[^1] = 2;

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Welcome>(Wrap("welcome", payload)));
    }

    [Fact]
    public void An_unknown_enum_value_maps_to_the_unknown_member_when_one_exists()
    {
        var snapshot = new GraphSnapshot(1, [new GraphNode("a", GraphNodeKind.Phase), new GraphNode("b", (GraphNodeKind)99)], []);

        var decoded = _codec.DecodePayload<GraphSnapshot>(Wrap("x", _codec.EncodePayload(snapshot)));

        Assert.Equal(GraphNodeKind.Phase, decoded.Nodes[0].Kind);
        Assert.Equal(GraphNodeKind.Unspecified, decoded.Nodes[1].Kind);
    }

    [Fact]
    public void An_unknown_enum_value_without_an_unknown_member_is_a_wire_format_error()
    {
        var payload = _codec.EncodePayload(new GcMemberAdd(_gc, _gc, (PrincipalKind)99, "member"));

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<GcMemberAdd>(Wrap("gc.member.add", payload)));
    }

    [Fact]
    public void A_date_time_offset_keeps_its_offset()
    {
        var local = new DateTimeOffset(2026, 11, 19, 7, 36, 12, 345, TimeSpan.FromHours(5.5));
        var welcome = new Welcome(1, 0, "memorypack", _gc, local, 5, 15, new WireLimits(1, 2, 3), [], 1, null, null);

        var decoded = _codec.DecodePayload<Welcome>(Wrap("welcome", _codec.EncodePayload(welcome)));

        Assert.Equal(local, decoded.ServerTime);
        Assert.Equal(local.Offset, decoded.ServerTime.Offset);
    }

    [Fact]
    public void An_out_of_range_date_time_is_a_wire_format_error()
    {
        var welcome = new Welcome(1, 0, "memorypack", _gc, DateTimeOffset.UnixEpoch, 5, 15, new WireLimits(1, 2, 3), [], 1, null, null);
        var payload = _codec.EncodePayload(welcome).ToArray();
        // Overwrite the 16 date-time bytes with the maximum long ticks and a 0 offset: ticks beyond DateTime.MaxValue.
        var at = payload.AsSpan().IndexOf(BitConverter.GetBytes(DateTimeOffset.UnixEpoch.UtcTicks));
        Assert.True(at > 0);
        BitConverter.GetBytes(long.MaxValue).CopyTo(payload, at);

        Assert.Throws<WireFormatException>(() => _codec.DecodePayload<Welcome>(Wrap("welcome", payload)));
    }

    [Fact]
    public void An_envelope_with_an_oversized_string_is_rejected_by_the_string_cap()
    {
        var codec = new MemoryPackWireCodec(new WireDecodeLimits(MaxStringBytes: 16));
        var frame = Frame(_codec, new Envelope(1, new string('t', 17), EnvelopeKind.Request, null, null, null, null, null, default));

        Assert.Throws<WireFormatException>(() => codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_with_an_unknown_kind_is_rejected()
    {
        // With no id, reply, stream, sequence, time or payload, k is the last byte of the frame.
        var frame = Frame(_codec, new Envelope(1, "ping", EnvelopeKind.Request, null, null, null, null, null, default));
        var at = frame.Length - 1;
        Assert.Equal((byte)EnvelopeKind.Request, frame[at]);
        frame[at] = 9;

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void An_envelope_payload_member_with_a_wrong_inner_length_is_rejected()
    {
        var payload = _codec.EncodePayload(new Ping("n")).ToArray();
        var frame = Frame(_codec, Wrap("ping", payload));
        // The payload array header is the 4 bytes just before the payload bytes; make it claim one byte less.
        var at = frame.AsSpan().LastIndexOf(payload) - sizeof(int);
        Assert.Equal(payload.Length, BitConverter.ToInt32(frame, at));
        BitConverter.GetBytes(payload.Length - 1).CopyTo(frame, at);

        Assert.Throws<WireFormatException>(() => _codec.Decode(frame));
    }

    [Fact]
    public void MemoryPack_is_smaller_than_json_for_the_same_replay_page()
    {
        var sample = PayloadSamples.Get("Resync");

        Assert.True(sample.Encode(_codec).Length < sample.Encode(new JsonWireCodec()).Length);
    }
}

[MemoryPackable(GenerateType.VersionTolerant, SerializeLayout.Explicit)]
internal sealed partial class StreamCursorMirror
{
    [MemoryPackOrder(0)] public string S { get; set; } = "";

    [MemoryPackOrder(1)] public long After { get; set; }

    [MemoryPackOrder(2)] public long EditAfter { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant, SerializeLayout.Explicit)]
internal sealed partial class HelloMirror
{
    [MemoryPackOrder(0)] public int VersionMin { get; set; }

    [MemoryPackOrder(1)] public int VersionMax { get; set; }

    [MemoryPackOrder(2)] public int Minor { get; set; }

    [MemoryPackOrder(3)] public List<string> Codecs { get; set; } = [];

    [MemoryPackOrder(4)] public string DeviceId { get; set; } = "";

    [MemoryPackOrder(5)] public string DeviceClass { get; set; } = "";

    [MemoryPackOrder(6)] public string ClientBuild { get; set; } = "";

    [MemoryPackOrder(7)] public string? Credential { get; set; }

    [MemoryPackOrder(8)] public List<StreamCursorMirror>? Cursors { get; set; }
}

/// <summary>What an older build sent: <c>Hello</c> before the credential and cursors existed.</summary>
[MemoryPackable(GenerateType.VersionTolerant, SerializeLayout.Explicit)]
internal sealed partial class HelloV0
{
    [MemoryPackOrder(0)] public int VersionMin { get; set; }

    [MemoryPackOrder(1)] public int VersionMax { get; set; }

    [MemoryPackOrder(2)] public int Minor { get; set; }

    [MemoryPackOrder(3)] public List<string> Codecs { get; set; } = [];

    [MemoryPackOrder(4)] public string DeviceId { get; set; } = "";

    [MemoryPackOrder(5)] public string DeviceClass { get; set; } = "";

    [MemoryPackOrder(6)] public string ClientBuild { get; set; } = "";
}

/// <summary>What a newer build sends: <c>Hello</c> with two members appended.</summary>
[MemoryPackable(GenerateType.VersionTolerant, SerializeLayout.Explicit)]
internal sealed partial class HelloV2
{
    [MemoryPackOrder(0)] public int VersionMin { get; set; }

    [MemoryPackOrder(1)] public int VersionMax { get; set; }

    [MemoryPackOrder(2)] public int Minor { get; set; }

    [MemoryPackOrder(3)] public List<string> Codecs { get; set; } = [];

    [MemoryPackOrder(4)] public string DeviceId { get; set; } = "";

    [MemoryPackOrder(5)] public string DeviceClass { get; set; } = "";

    [MemoryPackOrder(6)] public string ClientBuild { get; set; } = "";

    [MemoryPackOrder(7)] public string? Credential { get; set; }

    [MemoryPackOrder(8)] public List<StreamCursorMirror>? Cursors { get; set; }

    [MemoryPackOrder(9)] public string? Future { get; set; }

    [MemoryPackOrder(10)] public int FutureNumber { get; set; }
}
