using System.Globalization;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Cloudflare;

namespace Bazaar.Server.MailBackends.Tests.Cloudflare;

public sealed class SignedQueueReceiverTests
{
    private static readonly byte[] _key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-30T12:00:00Z", CultureInfo.InvariantCulture);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] Batch(params (string Id, string Mailbox, string Raw)[] items)
    {
        var payload = new
        {
            items = items.Select(i => new { id = i.Id, mailbox = i.Mailbox, raw = i.Raw, received_at = _now }),
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Valid_signed_batch_decodes_to_raw_mail()
    {
        var clock = new FixedClock(_now);
        var receiver = new SignedQueueReceiver(_key, clock);
        var body = Batch(("e1", "ada@example.org", B64("From: x\r\n\r\nhi")));
        var ts = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = receiver.Sign(ts, body);

        var mail = receiver.Receive(ts, sig, body);

        var one = Assert.Single(mail);
        Assert.Equal("cf:e1", one.BackendId);
        Assert.Equal(new MailboxRef("example.org", "ada"), one.Mailbox);
        Assert.Equal("From: x\r\n\r\nhi", Encoding.UTF8.GetString(one.Bytes.Span));
    }

    [Fact]
    public void Bad_signature_is_refused()
    {
        var receiver = new SignedQueueReceiver(_key, new FixedClock(_now));
        var body = Batch(("e1", "ada@example.org", B64("hi")));
        var ts = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Assert.Throws<UnauthorizedAccessException>(() => receiver.Receive(ts, new string('0', 64), body));
    }

    [Fact]
    public void Batch_outside_the_window_is_refused()
    {
        var receiver = new SignedQueueReceiver(_key, new FixedClock(_now));
        var body = Batch(("e1", "ada@example.org", B64("hi")));
        var old = _now.AddMinutes(-10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = receiver.Sign(old, body);
        Assert.Throws<UnauthorizedAccessException>(() => receiver.Receive(old, sig, body));
    }

    [Fact]
    public void Replayed_batch_is_refused()
    {
        var receiver = new SignedQueueReceiver(_key, new FixedClock(_now));
        var body = Batch(("e1", "ada@example.org", B64("hi")));
        var ts = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = receiver.Sign(ts, body);
        receiver.Receive(ts, sig, body);
        Assert.Throws<UnauthorizedAccessException>(() => receiver.Receive(ts, sig, body));
    }

    [Fact]
    public void Short_key_is_refused()
    {
        var receiver = new SignedQueueReceiver(new byte[16], new FixedClock(_now));
        var ts = _now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Assert.Throws<InvalidOperationException>(() => receiver.Receive(ts, "x", Batch()));
    }

    private static byte[] Batch() => Encoding.UTF8.GetBytes("{\"items\":[]}");
}
