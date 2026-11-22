using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>Tests of the NUL-terminated DevTools framing helpers in the Abstractions project.</summary>
public sealed class DevToolsFramingTests
{
    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static string Text(byte[]? bytes) => System.Text.Encoding.UTF8.GetString(bytes ?? throw new InvalidOperationException("no message"));

    [Fact]
    public async Task A_message_is_written_with_one_nul_after_it()
    {
        using var ms = new MemoryStream();
        using var writer = new DevToolsMessageWriter(ms);

        await writer.WriteAsync(Utf8("{\"id\":1,\"method\":\"Browser.getVersion\"}"), TestContext.Current.CancellationToken);

        var bytes = ms.ToArray();
        Assert.Equal(0, bytes[^1]);
        Assert.Equal("{\"id\":1,\"method\":\"Browser.getVersion\"}", Text(bytes[..^1]));
        Assert.DoesNotContain((byte)0, bytes[..^1]);
    }

    [Fact]
    public async Task Writing_refuses_a_nul_inside_an_empty_message_and_an_oversize_one_without_writing_anything()
    {
        using var ms = new MemoryStream();
        using var writer = new DevToolsMessageWriter(ms, 16);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.WriteAsync(new byte[] { 0x7b, 0, 0x7d }, ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.WriteAsync(ReadOnlyMemory<byte>.Empty, ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.WriteAsync(Utf8(new string('a', 17)), ct));

        Assert.Equal(0, ms.Length);
        await writer.WriteAsync(Utf8(new string('a', 16)), ct);
        Assert.Equal(17, ms.Length);
    }

    [Fact]
    public async Task Concurrent_writes_never_interleave()
    {
        using var ms = new MemoryStream();
        using var writer = new DevToolsMessageWriter(ms);
        var ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => writer.WriteAsync(Utf8("{\"n\":" + i + "}"), ct).AsTask()));

        ms.Position = 0;
        var reader = new DevToolsMessageReader(ms);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        byte[]? m;
        while ((m = await reader.ReadAsync(ct)) is not null)
        {
            Assert.StartsWith("{\"n\":", Text(m), StringComparison.Ordinal);
            seen.Add(Text(m));
        }

        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public async Task Several_messages_in_one_chunk_are_split_and_a_clean_end_returns_null()
    {
        using var ms = new MemoryStream([.. Utf8("{\"a\":1}"), 0, .. Utf8("{\"b\":2}"), 0]);
        var reader = new DevToolsMessageReader(ms);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("{\"a\":1}", Text(await reader.ReadAsync(ct)));
        Assert.Equal("{\"b\":2}", Text(await reader.ReadAsync(ct)));
        Assert.Null(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task A_message_split_across_many_small_reads_is_reassembled()
    {
        var payload = "{\"id\":7,\"result\":\"" + new string('x', 30000) + "\"}";
        using var slow = new TrickleStream([.. Utf8(payload), 0], 7);
        var reader = new DevToolsMessageReader(slow);

        Assert.Equal(payload, Text(await reader.ReadAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Empty_frames_are_skipped()
    {
        using var ms = new MemoryStream([0, 0, .. Utf8("{\"a\":1}"), 0, 0]);
        var reader = new DevToolsMessageReader(ms);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("{\"a\":1}", Text(await reader.ReadAsync(ct)));
        Assert.Null(await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task A_message_over_the_limit_fails_the_reader_and_stays_failed()
    {
        using var ms = new MemoryStream([.. Utf8(new string('a', 100)), 0, .. Utf8("{}"), 0]);
        var reader = new DevToolsMessageReader(ms, 64);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<DevToolsFramingException>(async () => await reader.ReadAsync(ct));
        await Assert.ThrowsAsync<DevToolsFramingException>(async () => await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task A_message_exactly_at_the_limit_is_accepted()
    {
        using var ms = new MemoryStream([.. Utf8(new string('a', 64)), 0]);
        var reader = new DevToolsMessageReader(ms, 64);

        Assert.Equal(64, (await reader.ReadAsync(TestContext.Current.CancellationToken))!.Length);
    }

    [Fact]
    public async Task An_endless_message_is_cut_off_at_the_limit_without_reading_it_all()
    {
        using var endless = new EndlessStream();
        var reader = new DevToolsMessageReader(endless, 100_000);

        await Assert.ThrowsAsync<DevToolsFramingException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));

        // The reader stopped soon after the limit: it did not consume an unbounded amount.
        Assert.True(endless.BytesServed < 100_000 + 16 * 1024);
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_message_is_an_error()
    {
        using var ms = new MemoryStream(Utf8("{\"half\":"));
        var reader = new DevToolsMessageReader(ms);

        await Assert.ThrowsAsync<DevToolsFramingException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_default_limit_is_16_MiB_and_bad_limits_are_refused()
    {
        Assert.Equal(16 * 1024 * 1024, DevToolsFraming.DefaultMaxMessageBytes);
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevToolsMessageReader(ms, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevToolsMessageReader(ms, DevToolsFraming.HardMaxMessageBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DevToolsMessageWriter(ms, -1));
    }

    /// <summary>Serves a fixed array a few bytes at a time.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _step;
        private int _position;

        public TrickleStream(byte[] data, int step)
        {
            _data = data;
            _step = step;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(_step, buffer.Length), _data.Length - _position);
            _data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves 'a' for ever, and counts what it served.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            buffer.Fill((byte)'a');
            BytesServed += buffer.Length;
            return buffer.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
