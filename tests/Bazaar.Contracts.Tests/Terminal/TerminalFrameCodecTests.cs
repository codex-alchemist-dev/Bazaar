using System.Text;
using Bazaar.Contracts.Terminal;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Terminal;

public class TerminalFrameCodecTests
{
    private static readonly byte[] _hi = [104, 105];

    // One sample per frame type, named after its golden file.
    private static readonly (string Name, TerminalFrame Frame)[] _samples =
    [
        ("open.json", new TerminalOpen("t1", "agent-7", 120, 40)),
        ("open-human-shell.json", new TerminalOpen("t2", null, 80, 24)),
        ("opened.json", new TerminalOpened("t1", true, 1)),
        ("output.json", new TerminalOutput("t1", 1, _hi)),
        ("input.json", new TerminalInput("t1", [13])),
        ("resize.json", new TerminalResize("t1", 200, 60)),
        ("control.json", new TerminalControl("t1", "take")),
        ("replay.json", new TerminalReplay("t1", 5)),
        ("exit.json", new TerminalExit("t1", 0)),
        ("close.json", new TerminalClose("t1")),
        ("error.json", new TerminalError("t1", "not-found", "no such terminal")),
    ];

    public static TheoryData<string, TerminalFrame> GoldenFrames()
    {
        var data = new TheoryData<string, TerminalFrame>();
        foreach (var (name, frame) in _samples) data.Add(name, frame);
        return data;
    }

    public static TheoryData<TerminalFrame> AllFrames()
    {
        var data = new TheoryData<TerminalFrame>();
        foreach (var (_, frame) in _samples) data.Add(frame);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFrames))]
    public void Encoded_frame_matches_golden_bytes(string name, TerminalFrame frame)
    {
        var result = TerminalFrameCodec.Encode(frame);

        Assert.True(result.IsSuccess, result.Detail);
        GoldenFile.Check(name, Encoding.UTF8.GetString(result.Bytes!));
    }

    [Theory]
    [MemberData(nameof(AllFrames))]
    public void Every_frame_round_trips(TerminalFrame frame)
    {
        var encoded = TerminalFrameCodec.Encode(frame);
        Assert.True(encoded.IsSuccess, encoded.Detail);

        var decoded = TerminalFrameCodec.Decode(encoded.Bytes!);

        Assert.True(decoded.IsSuccess, decoded.Detail);
        Assert.Equal(frame.GetType(), decoded.Frame!.GetType());
        Assert.Equal(frame.TerminalId, decoded.Frame.TerminalId);
        // Byte arrays compare by reference in records, so compare the re-encoded bytes.
        Assert.Equal(encoded.Bytes, TerminalFrameCodec.Encode(decoded.Frame).Bytes);
    }

    [Fact]
    public void Type_discriminator_is_the_first_property_and_lowercase()
    {
        var result = TerminalFrameCodec.Encode(new TerminalInput("t1", _hi));

        Assert.StartsWith("{\"type\":\"input\"", Encoding.UTF8.GetString(result.Bytes!));
    }

    [Fact]
    public void Payload_bytes_are_base64_and_decode_by_value()
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes("{\"type\":\"output\",\"terminalId\":\"t1\",\"seq\":3,\"bytes\":\"AAEC/v8=\"}"));

        var output = Assert.IsType<TerminalOutput>(decoded.Frame);
        Assert.Equal(3, output.Seq);
        Assert.Equal([0, 1, 2, 254, 255], output.Bytes);
    }

    [Fact]
    public void Unknown_fields_are_skipped()
    {
        var json = "{\"type\":\"resize\",\"terminalId\":\"t1\",\"cols\":80,\"rows\":24,\"dpi\":96,\"extra\":{\"a\":[1,2]}}";

        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.True(decoded.IsSuccess, decoded.Detail);
        Assert.Equal(new TerminalResize("t1", 80, 24), decoded.Frame);
    }

    [Fact]
    public void Missing_optional_agent_id_decodes_as_null()
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes("{\"type\":\"open\",\"terminalId\":\"t1\",\"cols\":80,\"rows\":24}"));

        Assert.Null(Assert.IsType<TerminalOpen>(decoded.Frame).AgentId);
    }

    [Theory]
    [InlineData("{\"type\":\"banana\",\"terminalId\":\"t1\"}")]
    [InlineData("{\"type\":\"OUTPUT\",\"terminalId\":\"t1\"}")]
    [InlineData("{\"type\":\"\",\"terminalId\":\"t1\"}")]
    public void Unknown_type_is_rejected(string json)
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(TerminalFrameFault.UnknownType, decoded.Fault);
    }

    [Theory]
    [InlineData("{\"terminalId\":\"t1\"}")]
    [InlineData("{\"type\":7,\"terminalId\":\"t1\"}")]
    [InlineData("{\"type\":null,\"terminalId\":\"t1\"}")]
    public void Missing_or_non_string_type_is_rejected(string json)
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TerminalFrameFault.MissingType, decoded.Fault);
    }

    [Theory]
    [InlineData("{\"type\":\"open\",\"cols\":80,\"rows\":24}", "terminalId")]
    [InlineData("{\"type\":\"open\",\"terminalId\":\"t1\",\"rows\":24}", "cols")]
    [InlineData("{\"type\":\"open\",\"terminalId\":null,\"cols\":80,\"rows\":24}", "terminalId")]
    [InlineData("{\"type\":\"opened\",\"terminalId\":\"t1\",\"writerHeld\":true}", "replayFrom")]
    [InlineData("{\"type\":\"output\",\"terminalId\":\"t1\",\"bytes\":\"aGk=\"}", "seq")]
    [InlineData("{\"type\":\"input\",\"terminalId\":\"t1\"}", "bytes")]
    [InlineData("{\"type\":\"control\",\"terminalId\":\"t1\"}", "action")]
    [InlineData("{\"type\":\"replay\",\"terminalId\":\"t1\"}", "fromSeq")]
    [InlineData("{\"type\":\"exit\",\"terminalId\":\"t1\"}", "code")]
    [InlineData("{\"type\":\"close\"}", "terminalId")]
    [InlineData("{\"type\":\"error\",\"terminalId\":\"t1\",\"code\":\"x\"}", "message")]
    public void Missing_required_field_is_rejected(string json, string field)
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TerminalFrameFault.MissingField, decoded.Fault);
        Assert.Contains(field, decoded.Detail);
    }

    [Theory]
    [InlineData("{\"type\":\"output\",\"terminalId\":\"t1\",\"seq\":\"one\",\"bytes\":\"aGk=\"}")]
    [InlineData("{\"type\":\"output\",\"terminalId\":\"t1\",\"seq\":1,\"bytes\":\"!!!\"}")]
    [InlineData("{\"type\":\"output\",\"terminalId\":\"t1\",\"seq\":1,\"bytes\":12}")]
    [InlineData("{\"type\":\"resize\",\"terminalId\":\"t1\",\"cols\":99999999999999999999,\"rows\":1}")]
    [InlineData("{\"type\":\"opened\",\"terminalId\":\"t1\",\"writerHeld\":\"yes\",\"replayFrom\":1}")]
    public void Wrong_field_types_are_rejected_without_throwing(string json)
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(TerminalFrameFault.InvalidField, decoded.Fault);
    }

    [Fact]
    public void Output_payload_of_exactly_16_KiB_is_accepted()
    {
        var bytes = new byte[TerminalFrameCodec.MaxPayloadBytes];
        var encoded = TerminalFrameCodec.Encode(new TerminalOutput("t1", 1, bytes));
        Assert.True(encoded.IsSuccess, encoded.Detail);

        var decoded = TerminalFrameCodec.Decode(encoded.Bytes!);

        Assert.True(decoded.IsSuccess, decoded.Detail);
        Assert.Equal(bytes.Length, ((TerminalOutput)decoded.Frame!).Bytes.Length);
    }

    [Fact]
    public void Oversize_output_is_refused_on_encode()
    {
        var result = TerminalFrameCodec.Encode(new TerminalOutput("t1", 1, new byte[TerminalFrameCodec.MaxPayloadBytes + 1]));

        Assert.False(result.IsSuccess);
        Assert.Equal(TerminalFrameFault.Oversize, result.Fault);
    }

    [Fact]
    public void Oversize_input_is_refused_on_encode()
    {
        var result = TerminalFrameCodec.Encode(new TerminalInput("t1", new byte[TerminalFrameCodec.MaxPayloadBytes + 1]));

        Assert.Equal(TerminalFrameFault.Oversize, result.Fault);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    public void Oversize_payload_is_rejected_on_decode(string type)
    {
        var base64 = Convert.ToBase64String(new byte[TerminalFrameCodec.MaxPayloadBytes + 1]);
        var json = $"{{\"type\":\"{type}\",\"terminalId\":\"t1\",\"seq\":1,\"bytes\":\"{base64}\"}}";
        if (type == "input") json = $"{{\"type\":\"input\",\"terminalId\":\"t1\",\"bytes\":\"{base64}\"}}";

        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(TerminalFrameFault.Oversize, decoded.Fault);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("dot.id")]
    [InlineData("semi;colon")]
    [InlineData("unié")]
    public void Invalid_terminal_ids_are_refused_on_encode(string id)
    {
        var result = TerminalFrameCodec.Encode(new TerminalClose(id));

        Assert.Equal(TerminalFrameFault.InvalidTerminalId, result.Fault);
    }

    [Fact]
    public void Invalid_terminal_id_is_rejected_on_decode()
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes("{\"type\":\"close\",\"terminalId\":\"../etc\"}"));

        Assert.Equal(TerminalFrameFault.InvalidTerminalId, decoded.Fault);
    }

    [Fact]
    public void Terminal_id_of_64_characters_is_valid_and_65_is_not()
    {
        Assert.True(WireStreams.IsValidTerminalId(new string('a', 64)));
        Assert.False(WireStreams.IsValidTerminalId(new string('a', 65)));
    }

    [Fact]
    public void Unknown_frame_subclass_is_refused_on_encode()
    {
        var result = TerminalFrameCodec.Encode(new UnknownFrame("t1"));

        Assert.Equal(TerminalFrameFault.UnknownType, result.Fault);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{\"type\":\"output\"")]
    [InlineData("{\"type\":\"close\",\"terminalId\":\"t1\"} trailing")]
    [InlineData("{\"type\":\"close\",\"terminalId\":\"t1\",}")]
    public void Malformed_text_is_a_failed_result(string json)
    {
        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.False(decoded.IsSuccess);
        Assert.NotEqual(TerminalFrameFault.None, decoded.Fault);
    }

    [Fact]
    public void Deeply_nested_input_is_a_failed_result()
    {
        var json = new string('[', 5000) + new string(']', 5000);

        var decoded = TerminalFrameCodec.Decode(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TerminalFrameFault.Malformed, decoded.Fault);
    }

    [Fact]
    public void Random_and_truncated_bytes_never_throw()
    {
        var random = new Random(52);
        var seeds = _samples.Select(s => TerminalFrameCodec.Encode(s.Frame).Bytes!).ToList();

        for (var round = 0; round < 3000; round++)
        {
            byte[] input;
            if (round % 2 == 0)
            {
                input = new byte[random.Next(0, 300)];
                random.NextBytes(input);
            }
            else
            {
                var seed = seeds[random.Next(seeds.Count)];
                var cut = random.Next(0, seed.Length + 1);
                input = seed[..cut];
                if (input.Length > 0 && random.Next(3) == 0) input[random.Next(input.Length)] ^= (byte)random.Next(1, 256);
            }

            var decoded = TerminalFrameCodec.Decode(input);

            Assert.Equal(decoded.IsSuccess, decoded.Frame is not null);
            Assert.Equal(decoded.IsSuccess, decoded.Fault == TerminalFrameFault.None);
        }
    }

    [Fact]
    public void Terminal_stream_name_round_trips()
    {
        var stream = WireStreams.Terminal("term-01_A");

        Assert.Equal("pane:term-01_A", stream);
        Assert.True(WireStreams.TryParseTerminal(stream, out var id));
        Assert.Equal("term-01_A", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pane:")]
    [InlineData("pane:bad id")]
    [InlineData("gc:0196a6f0-0000-7000-8000-000000000001")]
    [InlineData("pane")]
    public void Terminal_stream_parse_rejects_non_terminal_names(string? stream)
    {
        Assert.False(WireStreams.TryParseTerminal(stream, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/id")]
    public void Terminal_stream_name_refuses_invalid_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => WireStreams.Terminal(id));
    }

    private sealed record UnknownFrame(string TerminalId) : TerminalFrame(TerminalId);
}
