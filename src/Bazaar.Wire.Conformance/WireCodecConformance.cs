using System.Buffers;
using System.Reflection;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Xunit;

namespace Bazaar.Wire.Conformance;

/// <summary>
/// The conformance suite every wire codec must pass (doc 90 Tests and section 3 rules): round trip of every registered payload,
/// unknown envelope and payload fields ignored, unknown message types left opaque, decode limits enforced, and hostile input
/// failing only with <see cref="WireFormatException"/>. A codec's test project inherits this class and supplies the three hooks.
/// </summary>
public abstract class WireCodecConformance
{
    internal const int FuzzIterations = 2000;
    internal const int FuzzSeed = 90;

    private static readonly DateTimeOffset _at = new(2026, 11, 19, 5, 36, 12, 345, TimeSpan.Zero);
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    /// <summary>The codec name negotiated in <c>hello</c>.</summary>
    protected abstract string ExpectedName { get; }

    /// <summary>True when frames are binary WebSocket messages.</summary>
    protected abstract bool ExpectedIsBinary { get; }

    /// <summary>Creates the codec under test with the given decode limits.</summary>
    protected abstract IWireCodec CreateCodec(WireDecodeLimits limits);

    /// <summary>
    /// Returns <paramref name="encodedObject"/>, one encoded top-level object (a frame or a payload), with one extra field
    /// <c>x-future</c> added whose value nests arrays <paramref name="nestingDepth"/> levels deep. Used for the unknown-field tests.
    /// </summary>
    protected abstract byte[] AddUnknownField(ReadOnlyMemory<byte> encodedObject, int nestingDepth);

    /// <summary>
    /// True for a codec whose unknown fields are skipped by length without being parsed (MemoryPack's version-tolerant objects), so
    /// a deeply nested unknown field costs nothing and is not a nesting violation. Such a codec must still either read the frame or
    /// fail with <see cref="WireFormatException"/> on the nesting-bomb tests, never crash. Self-describing codecs leave this false.
    /// </summary>
    protected virtual bool UnknownFieldsAreOpaque => false;

    private IWireCodec Codec => CreateCodec(WireDecodeLimits.Default);

    [Fact]
    public void Codec_identifies_itself()
    {
        Assert.Equal(ExpectedName, Codec.Name);
        Assert.Equal(ExpectedIsBinary, Codec.IsBinary);
    }

    [Fact]
    public void Every_payload_record_in_contracts_has_a_sample()
    {
        var marked = typeof(WirePayloadAttribute).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<WirePayloadAttribute>().Any())
            .ToHashSet();
        var sampled = PayloadSamples.All.Select(s => s.Type).ToHashSet();

        Assert.Empty(marked.Except(sampled).Select(t => t.Name));
        Assert.Empty(sampled.Except(marked).Select(t => t.Name));
        Assert.NotEmpty(marked);
    }

    [Fact]
    public void Message_types_declared_on_payloads_are_known_wire_types()
    {
        var known = typeof(WireTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var declared = typeof(WirePayloadAttribute).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<WirePayloadAttribute>())
            .Select(a => a.MessageType)
            .Where(m => m is not null)
            .Cast<string>();

        Assert.Empty(declared.Where(m => !known.Contains(m)));
    }

    [Theory]
    [MemberData(nameof(PayloadSamples.Names), MemberType = typeof(PayloadSamples))]
    public void Payload_round_trips(string name)
    {
        var sample = PayloadSamples.Get(name);
        var codec = Codec;
        var encoded = sample.Encode(codec);
        var decoded = sample.Decode(codec, Envelope(sample, encoded));

        Assert.IsType(sample.Type, decoded);
        Assert.Equal(Canonical(sample), CanonicalOf(sample, decoded));
        Assert.True(encoded.Span.SequenceEqual(sample.Encode(codec).Span), "Encoding is not deterministic.");
    }

    [Theory]
    [MemberData(nameof(PayloadSamples.Names), MemberType = typeof(PayloadSamples))]
    public void Frame_with_payload_round_trips_and_keeps_payload_bytes(string name)
    {
        var sample = PayloadSamples.Get(name);
        var codec = Codec;
        var payload = sample.Encode(codec);
        var sent = Envelope(sample, payload);

        var received = codec.Decode(Frame(codec, sent));

        AssertEnvelopeEqual(sent, received);
        Assert.True(received.P.Span.SequenceEqual(payload.Span), "The payload bytes changed on the way through the envelope.");
        Assert.Equal(Canonical(sample), CanonicalOf(sample, sample.Decode(codec, received)));
    }

    [Fact]
    public void Envelope_round_trips_every_field_and_kind()
    {
        var codec = Codec;
        var payload = codec.EncodePayload(new Ping("n"));
        foreach (var kind in Enum.GetValues<EnvelopeKind>())
        {
            var full = new Envelope(1, "ping", kind, "id-1", "re-1", "gc:" + _gc, 123_456_789_012, _at, payload);
            AssertEnvelopeEqual(full, codec.Decode(Frame(codec, full)));

            var minimal = new Envelope(1, "ping", kind, null, null, null, null, null, default);
            AssertEnvelopeEqual(minimal, codec.Decode(Frame(codec, minimal)));
        }
    }

    [Fact]
    public void Envelope_time_is_read_back_as_the_same_instant()
    {
        var codec = Codec;
        var local = new DateTimeOffset(2026, 11, 19, 7, 36, 12, 345, TimeSpan.FromHours(2));
        var sent = new Envelope(1, "ping", EnvelopeKind.Event, null, null, null, null, local, default);

        var received = codec.Decode(Frame(codec, sent));

        Assert.Equal(local.UtcDateTime, received.Ts!.Value.UtcDateTime);
    }

    [Fact]
    public void Absent_optional_fields_read_as_null()
    {
        var codec = Codec;
        var hello = new Hello(1, 1, 0, ["json"], "d", "c", "b", null, null);

        var decoded = codec.DecodePayload<Hello>(Envelope("hello", codec.EncodePayload(hello)));

        Assert.Null(decoded.Credential);
        Assert.Null(decoded.Cursors);
    }

    [Fact]
    public void Unknown_envelope_field_is_ignored()
    {
        var codec = Codec;
        var payload = codec.EncodePayload(new Ping("n"));
        var sent = new Envelope(1, "ping", EnvelopeKind.Request, "id-1", null, null, null, null, payload);
        var frame = AddUnknownField(Frame(codec, sent), 3);

        var received = codec.Decode(frame);

        AssertEnvelopeEqual(sent, received);
        Assert.Equal("n", codec.DecodePayload<Ping>(received).Nonce);
    }

    [Fact]
    public void Unknown_payload_field_is_ignored()
    {
        var codec = Codec;
        var sample = PayloadSamples.Get("GcMemberAdd");
        var payload = AddUnknownField(sample.Encode(codec), 3);

        var decoded = sample.Decode(codec, Envelope(sample, payload));

        Assert.Equal(Canonical(sample), CanonicalOf(sample, decoded));
    }

    [Fact]
    public void Unknown_message_type_leaves_the_payload_opaque()
    {
        var codec = Codec;
        // A payload of a type this build has never heard of: any well-formed object, here with nesting and an unknown shape.
        var future = AddUnknownField(codec.EncodePayload(new Ping(null)), 5);
        var sent = new Envelope(1, "future.thing", EnvelopeKind.Request, "id-9", null, null, null, null, future);

        var received = codec.Decode(Frame(codec, sent));

        Assert.Equal("future.thing", received.T);
        Assert.True(received.P.Span.SequenceEqual(future), "The payload of an unknown message type must come back byte for byte.");
    }

    [Fact]
    public void Unregistered_payload_type_is_not_supported()
    {
        var codec = Codec;

        Assert.Throws<NotSupportedException>(() => codec.EncodePayload("text"));
        Assert.Throws<NotSupportedException>(() => codec.DecodePayload<string>(Envelope("x", codec.EncodePayload(new Ping(null)))));
    }

    [Fact]
    public void Message_without_payload_cannot_be_decoded_as_one()
    {
        var codec = Codec;

        Assert.Throws<WireFormatException>(() => codec.DecodePayload<Ping>(Envelope("ping", default)));
    }

    [Fact]
    public void Empty_and_garbage_frames_are_rejected()
    {
        var codec = Codec;

        Assert.Throws<WireFormatException>(() => codec.Decode([]));
        foreach (var bytes in new byte[][] { [0], [0xFF], [0x7B], [0x80], [0xA0], "null"u8.ToArray(), "{}"u8.ToArray(), "[]"u8.ToArray(), [1, 2, 3, 4, 5, 6, 7, 8] })
        {
            ExpectOnlyWireFormat(() => codec.Decode(bytes), "garbage " + Convert.ToHexString(bytes));
        }
    }

    [Fact]
    public void Every_strict_prefix_of_a_frame_is_rejected()
    {
        var codec = Codec;
        foreach (var name in new[] { "Resync", "GcMemberAdd", "Welcome", "TileFrame" })
        {
            var sample = PayloadSamples.Get(name);
            var frame = Frame(codec, Envelope(sample, sample.Encode(codec)));
            for (var length = 0; length < frame.Length; length++)
            {
                var prefix = frame.AsSpan(0, length).ToArray();
                Assert.Throws<WireFormatException>(() => codec.Decode(prefix));
            }
        }
    }

    [Fact]
    public void Data_after_the_frame_is_rejected()
    {
        var codec = Codec;
        var frame = Frame(codec, Envelope("ping", codec.EncodePayload(new Ping(null))));

        Assert.Throws<WireFormatException>(() => codec.Decode([.. frame, .. frame]));
        Assert.Throws<WireFormatException>(() => codec.Decode([.. frame, 0x00]));
    }

    [Fact]
    public void Mutated_and_truncated_frames_only_fail_with_wire_format_exception()
    {
        var codec = Codec;
        var samples = PayloadSamples.All;
        var frames = samples.Select(s => Frame(codec, Envelope(s, s.Encode(codec)))).ToArray();
        var payloads = samples.Select(s => s.Encode(codec).ToArray()).ToArray();
        var random = new Random(FuzzSeed);

        for (var i = 0; i < FuzzIterations; i++)
        {
            var index = random.Next(samples.Count);
            var sample = samples[index];
            var onFrame = i % 2 == 0;
            var mutated = Mutate(random, onFrame ? frames[index] : payloads[index], frames[random.Next(frames.Length)]);
            var label = $"iteration {i} seed {FuzzSeed} sample {sample.Name} on {(onFrame ? "frame" : "payload")}: {Convert.ToHexString(mutated)}";

            ExpectOnlyWireFormat(
                () =>
                {
                    var envelope = onFrame ? codec.Decode(mutated) : Envelope(sample, mutated);
                    _ = sample.Decode(codec, envelope);
                },
                label);
        }
    }

    [Fact]
    public void Nesting_deeper_than_the_cap_is_rejected_without_exhausting_the_stack()
    {
        var codec = Codec;
        var payload = codec.EncodePayload(new Ping(null));
        var frame = Frame(codec, Envelope("ping", payload));

        if (UnknownFieldsAreOpaque)
        {
            ExpectOnlyWireFormat(() => codec.Decode(AddUnknownField(frame, 100_000)), "nesting bomb in an opaque frame field");
            ExpectOnlyWireFormat(() => codec.DecodePayload<Ping>(Envelope("ping", AddUnknownField(payload, 100_000))), "nesting bomb in an opaque payload field");
            return;
        }

        Assert.Throws<WireFormatException>(() => codec.Decode(AddUnknownField(frame, 100_000)));
        Assert.Throws<WireFormatException>(() => codec.DecodePayload<Ping>(Envelope("ping", AddUnknownField(payload, 100_000))));
    }

    [Fact]
    public void Nesting_depth_is_capped_at_the_limit()
    {
        var withMentions = new GcSend(_gc, "hi", [_gc], null);
        var ok = CreateCodec(new WireDecodeLimits(MaxDepth: 3));
        var tight = CreateCodec(new WireDecodeLimits(MaxDepth: 2));
        var frame = Frame(ok, Envelope("gc.send", ok.EncodePayload(withMentions)));

        Assert.Equal(withMentions.Text, ok.DecodePayload<GcSend>(ok.Decode(frame)).Text);
        Assert.Throws<WireFormatException>(() => tight.DecodePayload<GcSend>(tight.Decode(frame)));
    }

    [Fact]
    public void String_length_is_capped_at_the_limit()
    {
        var codec = CreateCodec(new WireDecodeLimits(MaxStringBytes: 64));
        var atLimit = Frame(codec, Envelope("gc.send", codec.EncodePayload(new GcSend(_gc, new string('a', 64), null, null))));
        var overLimit = Frame(codec, Envelope("gc.send", codec.EncodePayload(new GcSend(_gc, new string('a', 65), null, null))));

        Assert.Equal(64, codec.DecodePayload<GcSend>(codec.Decode(atLimit)).Text.Length);
        Assert.Throws<WireFormatException>(() => codec.DecodePayload<GcSend>(codec.Decode(overLimit)));
    }

    [Fact]
    public void Multi_byte_string_length_is_counted_in_bytes()
    {
        var codec = CreateCodec(new WireDecodeLimits(MaxStringBytes: 64));
        var tooLong = Frame(codec, Envelope("gc.send", codec.EncodePayload(new GcSend(_gc, new string('\u00e9', 40), null, null))));

        Assert.Throws<WireFormatException>(() => codec.DecodePayload<GcSend>(codec.Decode(tooLong)));
    }

    [Fact]
    public void Collection_length_is_capped_at_the_limit()
    {
        var codec = CreateCodec(new WireDecodeLimits(MaxCollectionItems: 8));
        var atLimit = Frame(codec, Envelope("unsubscribe", codec.EncodePayload(new Unsubscribe(Enumerable.Repeat("s", 8).ToArray()))));
        var overLimit = Frame(codec, Envelope("unsubscribe", codec.EncodePayload(new Unsubscribe(Enumerable.Repeat("s", 9).ToArray()))));

        Assert.Equal(8, codec.DecodePayload<Unsubscribe>(codec.Decode(atLimit)).Streams.Count);
        Assert.Throws<WireFormatException>(() => codec.DecodePayload<Unsubscribe>(codec.Decode(overLimit)));
    }

    [Fact]
    public void The_default_limits_are_the_doc_90_numbers()
    {
        Assert.Equal(32, WireDecodeLimits.Default.MaxDepth);
        Assert.Equal(1_048_576, WireDecodeLimits.Default.MaxStringBytes);
        Assert.Equal(100_000, WireDecodeLimits.Default.MaxCollectionItems);
    }

    [Fact]
    public void A_collection_over_the_default_cap_is_rejected()
    {
        var codec = Codec;
        var big = new Unsubscribe(new string[WireDecodeLimits.DefaultMaxCollectionItems + 1].Select(_ => "s").ToArray());
        var frame = Frame(codec, Envelope("unsubscribe", codec.EncodePayload(big)));

        Assert.Throws<WireFormatException>(() => codec.DecodePayload<Unsubscribe>(codec.Decode(frame)));
    }

    private static Envelope Envelope(PayloadSample sample, ReadOnlyMemory<byte> payload) =>
        new(1, sample.MessageType, EnvelopeKind.Event, "id-1", null, "gc:" + _gc, 12, _at, payload);

    private static Envelope Envelope(string type, ReadOnlyMemory<byte> payload) =>
        new(1, type, EnvelopeKind.Request, "id-1", null, null, null, null, payload);

    private static byte[] Frame(IWireCodec codec, in Envelope envelope)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Encode(envelope, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static string Canonical(PayloadSample sample) => System.Text.Encoding.UTF8.GetString(sample.CanonicalJson().Span);

    private static string CanonicalOf(PayloadSample sample, object decoded)
    {
        var method = typeof(WireCodecConformance).GetMethod(nameof(JsonOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(sample.Type);
        return (string)method.Invoke(null, [decoded])!;
    }

    private static string JsonOf<T>(T value) => System.Text.Encoding.UTF8.GetString(new JsonWireCodec().EncodePayload(value).Span);

    private static void AssertEnvelopeEqual(Envelope expected, Envelope actual)
    {
        Assert.Equal(expected.V, actual.V);
        Assert.Equal(expected.T, actual.T);
        Assert.Equal(expected.K, actual.K);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Re, actual.Re);
        Assert.Equal(expected.S, actual.S);
        Assert.Equal(expected.N, actual.N);
        Assert.Equal(expected.Ts?.UtcDateTime, actual.Ts?.UtcDateTime);
        Assert.True(expected.P.Span.SequenceEqual(actual.P.Span), "Payload bytes differ.");
    }

    private static void ExpectOnlyWireFormat(Action action, string label)
    {
        try
        {
            action();
        }
        catch (WireFormatException)
        {
        }
        catch (Exception ex)
        {
            Assert.Fail($"{label}\n{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static byte[] Mutate(Random random, byte[] original, byte[] donor)
    {
        var bytes = original.ToList();
        switch (random.Next(7))
        {
            case 0:
                return original.AsSpan(0, random.Next(original.Length)).ToArray();
            case 1:
                bytes[random.Next(bytes.Count)] ^= (byte)(1 << random.Next(8));
                break;
            case 2:
                bytes[random.Next(bytes.Count)] = (byte)random.Next(256);
                break;
            case 3:
                bytes.Insert(random.Next(bytes.Count + 1), (byte)random.Next(256));
                break;
            case 4:
                bytes.RemoveAt(random.Next(bytes.Count));
                break;
            case 5:
                {
                    var start = random.Next(bytes.Count);
                    var value = random.Next(2) == 0 ? (byte)0xFF : (byte)0x00;
                    for (var i = start; i < Math.Min(bytes.Count, start + 1 + random.Next(4)); i++) bytes[i] = value;
                    break;
                }

            default:
                {
                    var at = random.Next(bytes.Count);
                    var from = random.Next(donor.Length);
                    bytes.RemoveRange(at, Math.Min(bytes.Count - at, random.Next(8)));
                    bytes.InsertRange(at, donor.AsSpan(from, Math.Min(donor.Length - from, 1 + random.Next(16))).ToArray());
                    break;
                }
        }

        return [.. bytes];
    }
}
