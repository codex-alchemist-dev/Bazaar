using System.Buffers;
using System.Text;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Cbor;
using Bazaar.Wire.Conformance;
using Bazaar.Wire.Schema.Generated;
using System.Buffers;

namespace Bazaar.Wire.Schema.Tests;

/// <summary>
/// Golden frames (doc 90 section 5): one frame per payload sample for each codec, checked in under
/// <c>docs/wire/vectors/&lt;codec&gt;</c> with a text description. A codec change that alters a byte fails here,
/// and a foreign client round-trips the same files.
/// </summary>
public class GoldenFrameTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 19, 5, 36, 12, 345, TimeSpan.Zero);

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var codec in new[] { "json", "cbor" })
            {
                foreach (var sample in PayloadSamples.All) data.Add(codec, sample.Name);
            }

            return data;
        }
    }

    private static IWireCodec Create(string name) => name == "json" ? new JsonWireCodec() : new CborWireCodec();

    private static string FileName(string sampleName)
    {
        var sb = new StringBuilder();
        foreach (var c in sampleName) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-').Replace("--", "-");
    }

    private static Envelope EnvelopeOf(PayloadSample sample, ReadOnlyMemory<byte> payload) =>
        new(WireProtocol.Major, sample.MessageType, EnvelopeKind.Event, "vector-1", null, "gc:0196a6f0-0000-7000-8000-000000000001", 7, _at, payload);

    private static byte[] Frame(IWireCodec codec, PayloadSample sample)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Encode(EnvelopeOf(sample, sample.Encode(codec)), writer);
        return writer.WrittenSpan.ToArray();
    }

    private static string Describe(string codecName, IWireCodec codec, PayloadSample sample, byte[] frame)
    {
        var sb = new StringBuilder();
        sb.Append("Golden frame: ").Append(sample.Name).Append('\n');
        sb.Append("Codec: ").Append(codecName).Append(codec.IsBinary ? " (binary)" : " (text)").Append('\n');
        sb.Append("Protocol: ").Append(WireProtocol.Major).Append('.').Append(WireProtocol.Minor).Append('\n');
        sb.Append("Schema hash: ").Append(WireSchemaArtifacts.SchemaHash).Append('\n');
        sb.Append("Payload record: ").Append(sample.Type.FullName).Append('\n');
        sb.Append("Envelope: v=").Append(WireProtocol.Major).Append(" t=").Append(sample.MessageType).Append(" k=evt id=vector-1 s=gc:0196a6f0-0000-7000-8000-000000000001 n=7 ts=2026-11-19T05:36:12.345Z\n");
        sb.Append("Frame length: ").Append(frame.Length).Append(" bytes\n");
        sb.Append("Payload as JSON: ").Append(Encoding.UTF8.GetString(sample.CanonicalJson().Span)).Append('\n');
        sb.Append("Frame hex:\n");
        for (var i = 0; i < frame.Length; i += 32)
        {
            sb.Append("  ").Append(Convert.ToHexString(frame.AsSpan(i, Math.Min(32, frame.Length - i)))).Append('\n');
        }

        sb.Append("\nA foreign client passes when it decodes the .bin file to this envelope and payload, and encodes them back to the same bytes.\n");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Codec_still_produces_the_checked_in_bytes(string codecName, string sampleName)
    {
        var codec = Create(codecName);
        var sample = PayloadSamples.Get(sampleName);
        var frame = Frame(codec, sample);
        var description = Describe(codecName, codec, sample, frame);
        var path = $"vectors/{codecName}/{FileName(sampleName)}";

        if (WireFiles.Updating)
        {
            WireFiles.Write(path + ".bin", frame);
            WireFiles.Write(path + ".txt", new UTF8Encoding(false).GetBytes(description));
            return;
        }

        var expected = WireFiles.ReadBytes(path + ".bin");
        Assert.True(expected is not null, $"Missing golden frame docs/wire/{path}.bin. Create it with: {WireFiles.UpdateCommand}");
        Assert.True(
            expected.AsSpan().SequenceEqual(frame),
            $"The {codecName} codec no longer produces the bytes in docs/wire/{path}.bin for {sampleName}. A changed encoding breaks published clients: "
            + $"fix the codec, or (for a deliberate protocol change) regenerate with: {WireFiles.UpdateCommand}");
        Assert.Equal(description, WireFiles.Read(path + ".txt").Replace("\r\n", "\n"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Checked_in_frame_decodes_to_the_sample(string codecName, string sampleName)
    {
        if (WireFiles.Updating) return;
        var codec = Create(codecName);
        var sample = PayloadSamples.Get(sampleName);
        var bytes = WireFiles.ReadBytes($"vectors/{codecName}/{FileName(sampleName)}.bin");
        Assert.NotNull(bytes);

        var envelope = codec.Decode(bytes);

        Assert.Equal(sample.MessageType, envelope.T);
        Assert.Equal(WireProtocol.Major, envelope.V);
        Assert.Equal(7, envelope.N);
        Assert.NotNull(sample.Decode(codec, envelope));
    }

    [Fact]
    public void Every_sample_has_a_distinct_file_name()
    {
        var names = PayloadSamples.All.Select(s => FileName(s.Name)).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
