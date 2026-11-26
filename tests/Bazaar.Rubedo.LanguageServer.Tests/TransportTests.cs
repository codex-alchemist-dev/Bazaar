using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Rubedo.LanguageServer;

namespace Bazaar.Rubedo.LanguageServer.Tests;

public sealed class TransportTests
{
    private static byte[] Frame(string json) => LanguageServerHost.Frame(json);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
    private const string Shutdown = """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""";
    private const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";
    private const string _init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
    private const string _shutdown = """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""";
    private const string _exit = """{"jsonrpc":"2.0","method":"exit"}""";

    private static async Task<(int Code, List<JsonObject> Replies)> RunAsync(Stream input)
    {
        using var output = new MemoryStream();
        int code = await LanguageServerHost.RunAsync(input, output, cancellationToken: TestContext.Current.CancellationToken);
        var replies = new List<JsonObject>();
        output.Position = 0;
        var reader = new FrameReader(output);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken) is { } body)
        {
            replies.Add(JsonNode.Parse(body)!.AsObject());
        }

        return (code, replies);
    }

    /// <summary>Hands out at most <c>chunk</c> bytes per read, to exercise split reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Full_session_over_streams_exits_zero()
    {
        byte[] input = Concat(Frame(Init), Frame(Shutdown), Frame(Exit));
        byte[] input = Concat(Frame(_init), Frame(_shutdown), Frame(_exit));
        (int code, List<JsonObject> replies) = await RunAsync(new MemoryStream(input));
        Assert.Equal(0, code);
        Assert.Equal(2, replies.Count);
        Assert.NotNull(replies[0]["result"]!["capabilities"]);
        Assert.Equal(2, (int?)replies[1]["id"]);
    }

    [Fact]
    public async Task Exit_without_shutdown_exits_one_and_eof_exits_one()
    {
        (int code, _) = await RunAsync(new MemoryStream(Concat(Frame(Init), Frame(Exit))));
        Assert.Equal(1, code);
        (int eof, _) = await RunAsync(new MemoryStream(Frame(Init)));
        (int code, _) = await RunAsync(new MemoryStream(Concat(Frame(_init), Frame(_exit))));
        Assert.Equal(1, code);
        (int eof, _) = await RunAsync(new MemoryStream(Frame(_init)));
        Assert.Equal(1, eof);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public async Task Messages_split_across_reads_are_reassembled(int chunk)
    {
        byte[] input = Concat(Frame(Init), Frame(Shutdown), Frame(Exit));
        byte[] input = Concat(Frame(_init), Frame(_shutdown), Frame(_exit));
        (int code, List<JsonObject> replies) = await RunAsync(new ChunkedStream(input, chunk));
        Assert.Equal(0, code);
        Assert.Equal(2, replies.Count);
    }

    [Fact]
    public async Task Several_messages_in_one_read_are_all_handled()
    {
        byte[] input = Concat(Frame(Init), Frame(Shutdown), Frame(Exit));
        byte[] input = Concat(Frame(_init), Frame(_shutdown), Frame(_exit));
        (int code, List<JsonObject> replies) = await RunAsync(new ChunkedStream(input, input.Length));
        Assert.Equal(0, code);
        Assert.Equal(2, replies.Count);
    }

    [Fact]
    public async Task Multibyte_body_is_measured_in_bytes()
    {
        string open = """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"u","languageId":"rubedo","version":1,"text":"\"héllo 😀\""}}}""";
        byte[] input = Concat(Frame(Init), Frame(open), Frame(Shutdown), Frame(Exit));
        byte[] input = Concat(Frame(_init), Frame(open), Frame(_shutdown), Frame(_exit));
        (int code, List<JsonObject> replies) = await RunAsync(new ChunkedStream(input, 5));
        Assert.Equal(0, code);
        Assert.Equal("textDocument/publishDiagnostics", (string?)replies[1]["method"]);
    }

    [Fact]
    public async Task Extra_headers_and_lowercase_names_are_accepted()
    {
        byte[] body = Encoding.UTF8.GetBytes(Init);
        byte[] header = Encoding.ASCII.GetBytes($"content-type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length: {body.Length}\r\n\r\n");
        (_, List<JsonObject> replies) = await RunAsync(new MemoryStream(Concat(header, body, Frame(Exit))));
        byte[] body = Encoding.UTF8.GetBytes(_init);
        byte[] header = Encoding.ASCII.GetBytes($"content-type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length: {body.Length}\r\n\r\n");
        (_, List<JsonObject> replies) = await RunAsync(new MemoryStream(Concat(header, body, Frame(_exit))));
        Assert.Single(replies);
    }

    [Fact]
    public async Task Bad_header_gets_a_parse_error_and_the_next_message_still_works()
    {
        byte[] bad = Encoding.ASCII.GetBytes("Content-Type: x\r\n\r\n");
        byte[] notNumber = Encoding.ASCII.GetBytes("Content-Length: abc\r\n\r\n");
        byte[] input = Concat(bad, notNumber, Frame(Init), Frame(Shutdown), Frame(Exit));
        byte[] input = Concat(bad, notNumber, Frame(_init), Frame(_shutdown), Frame(_exit));
        (int code, List<JsonObject> replies) = await RunAsync(new MemoryStream(input));
        Assert.Equal(0, code);
        Assert.Equal(4, replies.Count);
        Assert.Equal(LspErrorCodes.ParseError, (int?)replies[0]["error"]!["code"]);
        Assert.Equal(LspErrorCodes.ParseError, (int?)replies[1]["error"]!["code"]);
        Assert.NotNull(replies[2]["result"]);
    }

    [Fact]
    public async Task Oversized_length_stops_the_server_with_a_parse_error()
    {
        byte[] input = Encoding.ASCII.GetBytes($"Content-Length: {FrameReader.MaxBodyBytes + 1}\r\n\r\n");
        (int code, List<JsonObject> replies) = await RunAsync(new MemoryStream(input));
        Assert.Equal(1, code);
        Assert.Single(replies);
    }

    [Fact]
    public async Task Truncated_body_ends_cleanly()
    {
        byte[] whole = Frame(Init);
        byte[] whole = Frame(_init);
        (int code, List<JsonObject> replies) = await RunAsync(new MemoryStream(whole[..^5]));
        Assert.Equal(1, code);
        Assert.Empty(replies);
    }

    [Fact]
    public async Task Invalid_json_body_replies_parse_error_and_continues()
    {
        byte[] input = Concat(Frame("{oops"), Frame(Init), Frame(Exit));
        byte[] input = Concat(Frame("{oops"), Frame(_init), Frame(_exit));
        (_, List<JsonObject> replies) = await RunAsync(new MemoryStream(input));
        Assert.Equal(2, replies.Count);
        Assert.Equal(LspErrorCodes.ParseError, (int?)replies[0]["error"]!["code"]);
    }

    [Fact]
    public async Task Random_garbage_never_throws()
    {
        var random = new Random(20261120);
        for (int round = 0; round < 300; round++)
        {
            byte[] garbage = new byte[random.Next(0, 3000)];
            random.NextBytes(garbage);
            (int code, _) = await RunAsync(new ChunkedStream(garbage, random.Next(1, 200)));
            Assert.Equal(1, code);
        }
    }

    [Fact]
    public async Task Garbage_inside_valid_frames_never_throws()
    {
        var random = new Random(42);
        string[] seeds =
        [
            """{"jsonrpc":"2.0","id":1,"method":"textDocument/hover","params":{"textDocument":{"uri":"u"},"position":{"line":0,"character":0}}}""",
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"u","languageId":"rubedo","text":"1 +"}}}""",
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"u"},"contentChanges":[{"text":"x"}]}}""",
        ];
        using var data = new MemoryStream();
        byte[] init = Frame(Init);
        byte[] init = Frame(_init);
        data.Write(init);
        for (int i = 0; i < 2000; i++)
        {
            byte[] body = Encoding.UTF8.GetBytes(seeds[random.Next(seeds.Length)]);
            for (int k = random.Next(0, 4); k > 0; k--)
            {
                body[random.Next(body.Length)] = (byte)random.Next(256);
            }

            data.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            data.Write(body);
        }

        (_, List<JsonObject> replies) = await RunAsync(new MemoryStream(data.ToArray()));
        Assert.NotEmpty(replies);
    }

    [Fact]
    public void Core_never_throws_for_random_bodies()
    {
        var random = new Random(7);
        var core = new LanguageServerCore();
        for (int i = 0; i < 2000; i++)
        {
            byte[] body = new byte[random.Next(0, 200)];
            random.NextBytes(body);
            Assert.NotNull(core.Process(body));
        }
    }
}
