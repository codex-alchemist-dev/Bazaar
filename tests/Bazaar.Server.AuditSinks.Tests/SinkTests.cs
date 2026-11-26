using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.AuditSinks.Tests;

public sealed class SinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sinks-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class HmacSigner : IAuditSinkSigner
    {
        public string KeyId => "k1";

        public byte[] Sign(ReadOnlyMemory<byte> data) => HMACSHA256.HashData([7, 7, 7], data.Span);
    }

    private sealed class FakeSyslog : ISyslogTransport
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            Messages.Add(Encoding.UTF8.GetString(message.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLines : IAuditStreamWriter
    {
        public List<string> Lines { get; } = [];

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            Lines.Add(line);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeKeys(byte[] key) : IAuditSinkKeyProvider
    {
        public Task<AuditSinkKey> GetReplicaKeyAsync(CancellationToken ct) => Task.FromResult(new AuditSinkKey("recovery-1", key));
    }

    private sealed class FakeReplica : IAuditReplicaWriter
    {
        public Dictionary<string, byte[]> Objects { get; } = [];

        public Task WriteAsync(string objectName, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Objects[objectName] = data.ToArray();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Replica_writer_port_has_no_read_back_member()
    {
        var members = typeof(IAuditReplicaWriter).GetMethods().Select(m => m.Name).ToList();
        Assert.Equal(["WriteAsync"], members);
    }

    [Fact]
    public async Task File_sink_writes_one_signed_file_per_day_atomically_and_idempotently()
    {
        var sink = new SignedDailyFileSink(new SignedDailyFileOptions(_dir), new HmacSigner());
        var d1 = new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);
        var d2 = d1.AddMinutes(2);
        var rows = new[] { Make.Row(1, d1), Make.Row(2, d1), Make.Row(3, d2) };

        await sink.DeliverRowsAsync(rows, Ct);
        await sink.DeliverRowsAsync(rows, Ct); // at-least-once redelivery
        await sink.DeliverCheckpointsAsync([Make.Checkpoint(1, 2, d1)], Ct);

        var day1 = new DateOnly(2026, 11, 1);
        var lines = File.ReadAllLines(sink.DataPath(day1));
        Assert.Equal(3, lines.Length);
        Assert.Equal(["row", "row", "checkpoint"], lines.Select(l => JsonElement.Parse(l).GetProperty("type").GetString()));
        Assert.Single(File.ReadAllLines(sink.DataPath(new DateOnly(2026, 11, 2))));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        var bytes = File.ReadAllBytes(sink.DataPath(day1));
        var sig = JsonElement.Parse(File.ReadAllText(sink.SignaturePath(day1)));
        Assert.Equal("k1", sig.GetProperty("keyId").GetString());
        Assert.Equal(Convert.ToBase64String(new HmacSigner().Sign(bytes)), sig.GetProperty("signature").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sig.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task File_sink_keeps_ciphertext_payload_as_given()
    {
        var sink = new SignedDailyFileSink(new SignedDailyFileOptions(_dir), new HmacSigner());
        await sink.DeliverRowsAsync([Make.Row(1, payload: "{\"name\":\"enc:v1:XYZ\"}")], Ct);
        var text = File.ReadAllText(sink.DataPath(new DateOnly(2026, 11, 1)));
        Assert.Contains("enc:v1:XYZ", text);
    }

    [Fact]
    public void File_sink_requires_a_directory()
    {
        Assert.Throws<ArgumentException>(() => new SignedDailyFileSink(new SignedDailyFileOptions(" "), new HmacSigner()));
    }

    [Fact]
    public async Task Syslog_sink_formats_rfc5424_and_escapes_structured_data()
    {
        var transport = new FakeSyslog();
        var sink = new SyslogSink(new SyslogOptions("nuc 01"), transport);
        var row = Make.Row(7, payload: "{\"a\":1}") with { Actor = "we\"ird]\\actor" };

        await sink.DeliverRowsAsync([row], Ct);

        var m = Assert.Single(transport.Messages);
        Assert.StartsWith("<110>1 2026-11-01T10:00:00.000000Z nuc_01 bazaar - auth.signin [bazaar@32473 seq=\"7\" actor=\"we\\\"ird\\]\\\\actor\"", m);
        Assert.Contains("idem=\"" + row.IdempotencyKey + "\"", m);
        Assert.EndsWith("] {\"a\":1}", m);
    }

    [Fact]
    public async Task Syslog_tcp_framing_prefixes_octet_count_and_checkpoints_are_sent()
    {
        var transport = new FakeSyslog();
        var sink = new SyslogSink(new SyslogOptions("h", Framing: SyslogFraming.OctetCounting), transport);

        await sink.DeliverCheckpointsAsync([Make.Checkpoint(1, 3)], Ct);

        var m = Assert.Single(transport.Messages);
        var space = m.IndexOf(' ', StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(m[(space + 1)..]), int.Parse(m[..space], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("audit.checkpoint", m);
    }

    [Fact]
    public void Syslog_rejects_a_bad_facility()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyslogSink(new SyslogOptions("h", Facility: 24), new FakeSyslog()));
    }

    [Fact]
    public async Task Stream_sink_writes_otel_style_json_lines()
    {
        var lines = new FakeLines();
        var sink = new StreamSink(new StreamSinkOptions(), lines);
        var row = Make.Row(4);

        await sink.DeliverRowsAsync([row], Ct);
        await sink.DeliverCheckpointsAsync([Make.Checkpoint(1, 4)], Ct);

        var r = JsonElement.Parse(lines.Lines[0]);
        Assert.Equal("INFO", r.GetProperty("severity_text").GetString());
        Assert.Equal(row.Payload, r.GetProperty("body").GetString());
        Assert.Equal("bazaar", r.GetProperty("resource").GetProperty("service.name").GetString());
        Assert.Equal(4, r.GetProperty("attributes").GetProperty("bazaar.audit.seq").GetInt64());
        Assert.Equal(row.IdempotencyKey, r.GetProperty("attributes").GetProperty("bazaar.audit.idempotency_key").GetString());
        Assert.Equal("audit.checkpoint", JsonElement.Parse(lines.Lines[1]).GetProperty("attributes").GetProperty("bazaar.audit.kind").GetString());
    }

    [Fact]
    public async Task Replica_sink_encrypts_and_only_the_key_holder_can_open()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var writer = new FakeReplica();
        var sink = new EncryptedReplicaSink(new FakeKeys(key), writer);
        var rows = new[] { Make.Row(1, payload: "{\"name\":\"enc:v1:SECRETCIPHER\"}"), Make.Row(2) };

        await sink.DeliverRowsAsync(rows, Ct);
        await sink.DeliverRowsAsync(rows, Ct); // same batch overwrites itself
        await sink.DeliverCheckpointsAsync([Make.Checkpoint(1, 2)], Ct);

        Assert.Equal(2, writer.Objects.Count);
        var (name, blob) = writer.Objects.First(o => o.Key.StartsWith("audit/rows/", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRETCIPHER", Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain("auth.signin", Encoding.UTF8.GetString(blob));

        var plain = EncryptedReplicaSink.Open(blob, name, key);
        Assert.Equal(2, plain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("SECRETCIPHER", plain);

        Assert.Throws<AuthenticationTagMismatchException>(() => EncryptedReplicaSink.Open(blob, name + "x", key));
        Assert.Throws<AuthenticationTagMismatchException>(() => EncryptedReplicaSink.Open(blob, name, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task Replica_sink_refuses_a_short_key()
    {
        var sink = new EncryptedReplicaSink(new FakeKeys(new byte[16]), new FakeReplica());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.DeliverRowsAsync([Make.Row(1)], Ct));
    }
}
