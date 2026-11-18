using System.Text;
using System.Text.Json;
using Bazaar.Client.Athanor.Bridge;

namespace Bazaar.Client.Athanor.Tests.Bridge;

public sealed class BridgeCodecTests
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Encode_writes_the_type_discriminator_for_every_host_message()
    {
        var codec = new BridgeCodec();
        var cases = new (HostMessage Message, string Type)[]
        {
            (new OpenMessage(1, "ws:a.txt", "text", "rev1", "plaintext", false), "open"),
            (new CloseMessage(2, "ws:a.txt"), "close"),
            (new SetThemeMessage(3, "{\"base\":\"vs\"}"), "setTheme"),
            (new SetReadOnlyMessage(4, "ws:a.txt", true), "setReadOnly"),
            (new ShowDiffMessage(5, "a", "b", "csharp"), "showDiff"),
            (new ApplyEditMessage(6, "ws:a.txt", 1, 1, 1, 2, "x"), "applyEdit"),
            (new FocusMessage(7), "focus"),
        };

        foreach (var (message, type) in cases)
        {
            using JsonDocument document = JsonDocument.Parse(codec.Encode(message));
            Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
            Assert.Equal(message.Seq, document.RootElement.GetProperty("seq").GetInt64());
        }
    }

    [Fact]
    public void Host_messages_round_trip_through_the_wire_format()
    {
        var codec = new BridgeCodec();
        var messages = new HostMessage[]
        {
            new OpenMessage(1, "ws:src/Program.cs", "class C { }\n", "rev-9", "csharp", true),
            new CloseMessage(2, "ws:src/Program.cs"),
            new SetThemeMessage(3, "{\"base\":\"vs-dark\",\"colors\":{\"editor.background\":\"#101010\"}}"),
            new SetReadOnlyMessage(4, "ws:src/Program.cs", false),
            new ShowDiffMessage(5, "old\r\nline", "new \"quoted\"", "markdown"),
            new ApplyEditMessage(6, "ws:src/Program.cs", 2, 3, 2, 9, "replacement"),
            new FocusMessage(7),
        };

        foreach (HostMessage message in messages)
        {
            string json = codec.Encode(message);
            HostMessage? decoded = JsonSerializer.Deserialize<HostMessage>(json, WireOptions);
            Assert.Equal(message, decoded);
        }
    }

    [Fact]
    public void Theme_payload_is_carried_as_data_with_its_quotes_escaped()
    {
        var codec = new BridgeCodec();
        const string theme = "{\"rules\":[{\"token\":\"comment\",\"foreground\":\"6a9955\"}]}\"</script>";

        string json = codec.Encode(new SetThemeMessage(1, theme));
        HostMessage? decoded = JsonSerializer.Deserialize<HostMessage>(json, WireOptions);

        Assert.Equal(new SetThemeMessage(1, theme), decoded);
    }

    [Fact]
    public void Editor_messages_round_trip_through_decode()
    {
        var codec = new BridgeCodec();
        var messages = new EditorMessage[]
        {
            new ReadyMessage(1),
            new ChangedMessage(2, "ws:a.txt", 4, "hello\nworld", true),
            new SaveRequestedMessage(3, "ws:a.txt"),
            new KeyPressedMessage(4, "Escape", false, true, false, false),
            new SelectionMessage(5, "ws:a.txt", 1, 2, 3, 4),
            new ErrorMessage(6, "ws:a.txt", "render", "The model failed to load."),
            new ErrorMessage(7, null, "bridge", "A bridge fault without a document."),
        };

        foreach (EditorMessage message in messages)
        {
            string json = JsonSerializer.Serialize<EditorMessage>(message, WireOptions);

            Assert.True(codec.TryDecode(json, out EditorMessage? decoded, out string? error), error);
            Assert.Equal(message, decoded);
            Assert.Null(error);
        }
    }

    [Fact]
    public void Content_with_script_text_stays_data()
    {
        var codec = new BridgeCodec();
        const string content = "<script>alert('x')</script>\"; drop();";
        string json = JsonSerializer.Serialize<EditorMessage>(
            new ChangedMessage(1, "ws:a.html", 1, content, true),
            WireOptions);

        Assert.True(codec.TryDecode(json, out EditorMessage? decoded, out _));
        Assert.Equal(content, Assert.IsType<ChangedMessage>(decoded).Content);
    }

    [Fact]
    public void Decode_accepts_a_payload_at_the_limit()
    {
        string json = """{"type":"ready","seq":1}""";
        var codec = new BridgeCodec(Encoding.UTF8.GetByteCount(json));

        Assert.True(codec.TryDecode(json, out _, out _));
    }

    [Fact]
    public void Decode_rejects_a_payload_over_the_limit()
    {
        var codec = new BridgeCodec(maxPayloadBytes: 64);
        string json = JsonSerializer.Serialize<EditorMessage>(
            new ChangedMessage(1, "ws:a.txt", 1, new string('x', 200), false),
            WireOptions);

        Assert.False(codec.TryDecode(json, out EditorMessage? message, out string? error));
        Assert.Null(message);
        Assert.NotNull(error);
        Assert.Contains("size limit", error);
    }

    [Fact]
    public void Decode_counts_the_limit_in_utf8_bytes_not_characters()
    {
        // Each of these characters is three bytes in UTF-8, so the character count fits but the byte count does not.
        string json = """{"type":"ready","seq":1,"note":"€€€€€€€€€€€€€€€€€€€€"}""";
        var codec = new BridgeCodec(Encoding.UTF8.GetByteCount(json) - 1);

        Assert.False(codec.TryDecode(json, out _, out _));
    }

    [Fact]
    public void Encode_rejects_a_message_over_the_limit()
    {
        var codec = new BridgeCodec(maxPayloadBytes: 128);

        Assert.Throws<ArgumentException>(() => codec.Encode(new ShowDiffMessage(1, new string('a', 500), "b", "text")));
    }

    [Fact]
    public void Encode_rejects_a_zero_sequence_number()
    {
        var codec = new BridgeCodec();

        Assert.Throws<ArgumentException>(() => codec.Encode(new FocusMessage(0)));
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeCodec(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeCodec(-1));
    }

    [Fact]
    public void Default_limit_is_eight_mebibytes()
    {
        Assert.Equal(8L * 1024 * 1024, new BridgeCodec().MaxPayloadBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"type\":\"ready\",\"seq\":1")]
    [InlineData("{\"type\":\"ready\",\"seq\":1} trailing")]
    [InlineData("not json at all")]
    public void Decode_rejects_malformed_json(string json)
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(json, out EditorMessage? message, out string? error));
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_rejects_a_null_payload()
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(null!, out _, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"ready\"")]
    [InlineData("{\"seq\":1}")]
    [InlineData("{\"type\":7,\"seq\":1}")]
    [InlineData("{\"type\":null,\"seq\":1}")]
    public void Decode_rejects_a_payload_without_a_string_type(string json)
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(json, out _, out string? error));
        Assert.Equal("The payload has no message type.", error);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("")]
    [InlineData("READY")]
    [InlineData("open")]
    [InlineData("setTheme")]
    public void Decode_rejects_an_unknown_or_host_only_type(string type)
    {
        var codec = new BridgeCodec();
        string json = $"{{\"type\":\"{type}\",\"seq\":1}}";

        Assert.False(codec.TryDecode(json, out _, out string? error));
        Assert.Equal("The message type is not known.", error);
    }

    [Fact]
    public void Decode_rejects_a_field_of_the_wrong_type()
    {
        var codec = new BridgeCodec();
        const string json = """{"type":"changed","seq":1,"documentId":"ws:a.txt","version":"4","content":"x","dirty":true}""";

        Assert.False(codec.TryDecode(json, out _, out string? error));
        Assert.Equal("The message fields do not match its type.", error);
    }

    [Theory]
    [InlineData("""{"type":"changed","seq":1,"documentId":"ws:a.txt","content":"x","dirty":true}""")]
    [InlineData("""{"type":"changed","seq":1,"documentId":"ws:a.txt","version":1,"dirty":true}""")]
    [InlineData("""{"type":"keyPressed","seq":1,"key":"j","shift":false,"alt":false,"meta":false}""")]
    [InlineData("""{"type":"error","seq":1,"code":"x"}""")]
    [InlineData("""{"type":"selection","seq":1,"documentId":"ws:a.txt","startLine":1,"startColumn":1,"endLine":1}""")]
    public void Decode_rejects_a_message_missing_a_required_field(string json)
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(json, out EditorMessage? message, out string? error));
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_rejects_a_missing_sequence_number()
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode("""{"type":"ready"}""", out _, out string? error));
        Assert.Equal("The message fields do not match its type.", error);
    }

    [Theory]
    [InlineData("""{"type":"ready","seq":0}""")]
    [InlineData("""{"type":"ready","seq":-3}""")]
    public void Decode_rejects_a_sequence_number_below_one(string json)
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(json, out _, out string? error));
        Assert.Equal("The sequence number must be positive.", error);
    }

    [Fact]
    public void Decode_rejects_an_unknown_property()
    {
        var codec = new BridgeCodec();
        const string json = """{"type":"ready","seq":1,"script":"alert(1)"}""";

        Assert.False(codec.TryDecode(json, out _, out string? error));
        Assert.Equal("The message fields do not match its type.", error);
    }

    [Theory]
    [InlineData("""{"type":"changed","seq":1,"documentId":"","version":1,"content":"x","dirty":true}""")]
    [InlineData("""{"type":"changed","seq":1,"documentId":"   ","version":1,"content":"x","dirty":true}""")]
    [InlineData("""{"type":"changed","seq":1,"documentId":"ws:a.txt","version":-1,"content":"x","dirty":true}""")]
    [InlineData("""{"type":"changed","seq":1,"documentId":"ws:a.txt","version":1,"content":null,"dirty":true}""")]
    [InlineData("""{"type":"saveRequested","seq":1,"documentId":""}""")]
    [InlineData("""{"type":"keyPressed","seq":1,"key":"","shift":false,"alt":false,"meta":false,"ctrl":false}""")]
    [InlineData("""{"type":"keyPressed","seq":1,"key":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","shift":false,"alt":false,"meta":false,"ctrl":false}""")]
    [InlineData("""{"type":"selection","seq":1,"documentId":"ws:a.txt","startLine":0,"startColumn":1,"endLine":1,"endColumn":1}""")]
    [InlineData("""{"type":"selection","seq":1,"documentId":"ws:a.txt","startLine":1,"startColumn":1,"endLine":1,"endColumn":0}""")]
    [InlineData("""{"type":"error","seq":1,"documentId":"ws:a.txt","code":" ","message":"x"}""")]
    [InlineData("""{"type":"error","seq":1,"documentId":" ","code":"render","message":"x"}""")]
    [InlineData("""{"type":"error","seq":1,"documentId":null,"code":"render","message":null}""")]
    public void Decode_rejects_a_field_out_of_range(string json)
    {
        var codec = new BridgeCodec();

        Assert.False(codec.TryDecode(json, out EditorMessage? message, out string? error));
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_accepts_an_error_without_a_document()
    {
        var codec = new BridgeCodec();

        Assert.True(codec.TryDecode("""{"type":"error","seq":2,"documentId":null,"code":"bridge","message":"fault"}""", out EditorMessage? message, out _));
        Assert.Null(Assert.IsType<ErrorMessage>(message).DocumentId);
    }

    [Fact]
    public void Decode_accepts_an_empty_changed_document()
    {
        var codec = new BridgeCodec();

        Assert.True(codec.TryDecode("""{"type":"changed","seq":1,"documentId":"ws:a.txt","version":0,"content":"","dirty":false}""", out EditorMessage? message, out _));
        Assert.Equal(string.Empty, Assert.IsType<ChangedMessage>(message).Content);
    }

    [Fact]
    public void Decode_does_not_judge_sequence_order_without_a_guard()
    {
        var codec = new BridgeCodec();

        Assert.True(codec.TryDecode("""{"type":"ready","seq":9}""", out _, out _));
        Assert.True(codec.TryDecode("""{"type":"ready","seq":2}""", out _, out _));
    }

    [Fact]
    public void Guarded_decode_accepts_rising_sequence_numbers()
    {
        var codec = new BridgeCodec();
        var guard = new BridgeSequenceGuard();

        Assert.True(codec.TryDecode("""{"type":"ready","seq":1}""", guard, out EditorMessage? first, out _));
        Assert.True(codec.TryDecode("""{"type":"saveRequested","seq":2,"documentId":"ws:a.txt"}""", guard, out EditorMessage? second, out _));
        Assert.True(codec.TryDecode("""{"type":"ready","seq":10}""", guard, out EditorMessage? third, out _));

        Assert.Equal(1, first!.Seq);
        Assert.Equal(2, second!.Seq);
        Assert.Equal(10, third!.Seq);
    }

    [Theory]
    [InlineData("""{"type":"ready","seq":5}""")]
    [InlineData("""{"type":"ready","seq":4}""")]
    public void Guarded_decode_refuses_a_repeated_or_older_sequence_number(string json)
    {
        var codec = new BridgeCodec();
        var guard = new BridgeSequenceGuard();
        Assert.True(codec.TryDecode("""{"type":"ready","seq":5}""", guard, out _, out _));

        bool accepted = codec.TryDecode(json, guard, out EditorMessage? message, out string? error);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal("The sequence number is not increasing.", error);
        Assert.Equal(5, guard.LastSeq);
    }

    [Fact]
    public void Guarded_decode_does_not_move_the_guard_when_the_message_is_invalid()
    {
        var codec = new BridgeCodec();
        var guard = new BridgeSequenceGuard();

        Assert.False(codec.TryDecode("""{"type":"changed","seq":7,"documentId":"","version":1,"content":"x","dirty":true}""", guard, out _, out _));

        Assert.Equal(0, guard.LastSeq);
        Assert.True(codec.TryDecode("""{"type":"ready","seq":1}""", guard, out _, out _));
    }

    [Fact]
    public void Guarded_decode_refuses_an_unknown_type_without_moving_the_guard()
    {
        var codec = new BridgeCodec();
        var guard = new BridgeSequenceGuard();

        Assert.False(codec.TryDecode("""{"type":"eval","seq":3}""", guard, out _, out _));

        Assert.Equal(0, guard.LastSeq);
    }
}
