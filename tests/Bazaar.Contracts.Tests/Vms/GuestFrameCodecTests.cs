using System.Buffers.Binary;
using System.Text;
using Bazaar.Contracts.Vms;

namespace Bazaar.Contracts.Tests.Vms;

public class GuestFrameCodecTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    public static TheoryData<GuestMessage> AllMessages() =>
    [
        new Hello("0.3.0", "vm-7", "n0nce"),
        new Exec("ex-1", ["git", "status", "--short"], @"C:\work", ["PATH", "HOME"], 300),
        new Output("ex-1", OutputSource.Stdout, 0, [0, 1, 2, 254, 255]),
        new Output("ex-1", OutputSource.Stderr, 41, []),
        new Cancel("ex-1"),
        new Exit("ex-1", 0, false),
        new Exit("ex-2", -1, true),
        new PutFile("/mnt/workspace/a.txt", 12, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"),
        new GetFile("/mnt/workspace/a.txt"),
        new Heartbeat(),
        new PtyOpen("pty-1", 120, 40),
        new PtyData("pty-1", 3, [27, 91, 72]),
        new PtyResize("pty-1", 200, 60),
        new PtyClose("pty-1"),
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public async Task Every_message_type_round_trips(GuestMessage message)
    {
        await using var stream = new MemoryStream();
        await GuestFrameCodec.WriteAsync(stream, message, _ct);

        stream.Position = 0;
        var read = await GuestFrameCodec.ReadAsync(stream, _ct);

        Assert.NotNull(read);
        Assert.Equal(message.GetType(), read.GetType());
        Assert.Equal(stream.Length, stream.Position);

        // Records compare arrays and lists by reference, so compare the frames: re-encoding the decoded message
        // must give the same bytes.
        Assert.Equal(Frame(message), Frame(read));
    }

    [Fact]
    public async Task Byte_fields_round_trip_by_value()
    {
        await using var stream = new MemoryStream();
        await GuestFrameCodec.WriteAsync(stream, new Output("ex-1", OutputSource.Stderr, 9, [0, 1, 2, 254, 255]), _ct);
        await GuestFrameCodec.WriteAsync(stream, new PtyData("pty-1", 4, [27, 91]), _ct);

        stream.Position = 0;
        var output = Assert.IsType<Output>(await GuestFrameCodec.ReadAsync(stream, _ct));
        Assert.Equal(OutputSource.Stderr, output.Stream);
        Assert.Equal(9, output.Seq);
        Assert.Equal([0, 1, 2, 254, 255], output.Bytes);

        var data = Assert.IsType<PtyData>(await GuestFrameCodec.ReadAsync(stream, _ct));
        Assert.Equal(4, data.Seq);
        Assert.Equal([27, 91], data.Bytes);
    }

    [Fact]
    public void Frame_starts_with_big_endian_length_version_and_tag()
    {
        var frame = Frame(new Hello("0.3.0", "vm-7", "n"));
        var body = Encoding.UTF8.GetString(frame, 2 + 5 + 4, frame.Length - 2 - 5 - 4);

        Assert.Equal((uint)(frame.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(frame));
        Assert.Equal(1, frame[4]);
        Assert.Equal(5, frame[5]);
        Assert.Equal("hello", Encoding.ASCII.GetString(frame, 6, 5));
        Assert.Equal("{\"agentVersion\":\"0.3.0\",\"vmId\":\"vm-7\",\"nonce\":\"n\"}", body);
    }

    [Fact]
    public void Byte_fields_are_base64_strings_in_json()
    {
        var frame = Frame(new PtyData("pty-1", 0, [0, 1, 2, 254, 255]));
        var body = Encoding.UTF8.GetString(frame);

        Assert.Contains("\"bytes\":\"AAEC/v8=\"", body);
    }

    [Fact]
    public void Output_stream_is_written_as_a_name()
    {
        var frame = Frame(new Output("ex-1", OutputSource.Stderr, 0, []));

        Assert.Contains("\"stream\":\"Stderr\"", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public async Task Reads_consecutive_frames_then_returns_null_at_clean_end()
    {
        await using var stream = new MemoryStream();
        await GuestFrameCodec.WriteAsync(stream, new Heartbeat(), _ct);
        await GuestFrameCodec.WriteAsync(stream, new Cancel("ex-9"), _ct);

        stream.Position = 0;
        Assert.IsType<Heartbeat>(await GuestFrameCodec.ReadAsync(stream, _ct));
        Assert.Equal("ex-9", Assert.IsType<Cancel>(await GuestFrameCodec.ReadAsync(stream, _ct)).Id);
        Assert.Null(await GuestFrameCodec.ReadAsync(stream, _ct));
    }

    [Fact]
    public async Task Empty_stream_reads_as_null()
    {
        await using var stream = new MemoryStream();

        Assert.Null(await GuestFrameCodec.ReadAsync(stream, _ct));
    }

    [Fact]
    public async Task Unknown_envelope_fields_are_ignored()
    {
        var frame = Raw(1, "cancel", "{\"id\":\"ex-3\",\"extra\":[1,2]}");

        var cancel = Assert.IsType<Cancel>(await GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));

        Assert.Equal("ex-3", cancel.Id);
    }

    [Fact]
    public async Task Rejects_unknown_version()
    {
        var frame = Raw(2, "heartbeat", "{}");

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));

        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public async Task Rejects_unknown_type_tag()
    {
        var frame = Raw(1, "shutdown", "{}");

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));

        Assert.Contains("'shutdown'", ex.Message);
    }

    [Fact]
    public async Task Rejects_oversized_frame_on_read_without_reading_the_body()
    {
        // Only the length field is present; the reader must refuse before it tries to read the body.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, GuestFrameCodec.MaxFrameBytes + 1);

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(header), _ct));

        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public async Task Rejects_oversized_message_on_write_and_writes_nothing()
    {
        await using var stream = new MemoryStream();
        var big = new Exec("ex-1", ["echo", new string('x', GuestFrameCodec.MaxFrameBytes)], "/", [], 60);

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.WriteAsync(stream, big, _ct));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Accepts_frame_of_exactly_the_maximum_size()
    {
        // Cancel's body is {"id":"<id>"}, so the counted length is 2 + "cancel".Length + 9 + id.Length.
        var id = new string('a', GuestFrameCodec.MaxFrameBytes - (2 + 6 + 9));
        await using var stream = new MemoryStream();

        await GuestFrameCodec.WriteAsync(stream, new Cancel(id), _ct);

        stream.Position = 0;
        Assert.Equal(id, Assert.IsType<Cancel>(await GuestFrameCodec.ReadAsync(stream, _ct)).Id);
    }

    [Fact]
    public async Task Rejects_frame_one_byte_over_the_maximum_on_write()
    {
        var id = new string('a', GuestFrameCodec.MaxFrameBytes - (2 + 6 + 9) + 1);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.WriteAsync(stream, new Cancel(id), _ct));
    }

    [Fact]
    public async Task Rejects_truncated_length_field()
    {
        var partial = new byte[] { 0, 0 };

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(partial), _ct));

        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public async Task Rejects_truncated_body()
    {
        var frame = Frame(new Exit("ex-1", 0, false));
        var cut = frame[..^5];

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(cut), _ct));

        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public async Task Rejects_frame_too_short_for_its_header()
    {
        var frame = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 1);
        frame[4] = 1;

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));
    }

    [Fact]
    public async Task Rejects_tag_length_that_runs_past_the_frame()
    {
        var frame = new byte[] { 0, 0, 0, 3, 1, 200, (byte)'{' };

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));
    }

    [Fact]
    public async Task Rejects_body_that_does_not_match_its_type()
    {
        var frame = Raw(1, "exit", "{\"id\":\"ex-1\",\"code\":\"zero\",\"timedOut\":false}");

        var ex = await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));

        Assert.Contains("'exit'", ex.Message);
    }

    [Fact]
    public async Task Rejects_body_that_is_not_json()
    {
        var frame = Raw(1, "heartbeat", "{not json");

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));
    }

    [Fact]
    public async Task Rejects_null_body()
    {
        var frame = Raw(1, "cancel", "null");

        await Assert.ThrowsAsync<GuestFrameException>(() => GuestFrameCodec.ReadAsync(new MemoryStream(frame), _ct));
    }

    private static byte[] Frame(GuestMessage message)
    {
        using var stream = new MemoryStream();
        GuestFrameCodec.WriteAsync(stream, message, _ct).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    /// <summary>Builds a frame by hand so tests can break one field at a time.</summary>
    private static byte[] Raw(byte version, string tag, string json)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        var body = Encoding.UTF8.GetBytes(json);
        var counted = 2 + tagBytes.Length + body.Length;
        var frame = new byte[4 + counted];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)counted);
        frame[4] = version;
        frame[5] = (byte)tagBytes.Length;
        tagBytes.CopyTo(frame, 6);
        body.CopyTo(frame, 6 + tagBytes.Length);
        return frame;
    }
}
