using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bazaar.GitHub.Webhooks;

namespace Bazaar.GitHub.Tests.Webhooks;

/// <summary>Hostile signature headers and replayed deliveries racing each other (chaos round 2).</summary>
public class WebhookHostileInputTests
{
    private static readonly byte[] _secret = Encoding.UTF8.GetBytes("It's a Secret to Everybody");
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("Hello, World!");
    private const string _good = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    [Theory]
    [InlineData(" sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17 ")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17\n")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e1\0")]
    [InlineData("SHA256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha1=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=0x757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e")]
    [InlineData("sha256=٧٥٧107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17,sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("\ud800sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    public void A_header_that_is_not_exactly_the_digest_fails_and_never_throws(string header) =>
        Assert.False(WebhookSignature.Verify(_secret, _body, header));

    [Fact]
    public void A_huge_header_and_a_huge_body_do_not_throw()
    {
        Assert.False(WebhookSignature.Verify(_secret, _body, "sha256=" + new string('a', 1_000_000)));
        var big = new byte[20 * 1024 * 1024];
        var header = WebhookSignature.Compute(_secret, big);
        Assert.True(WebhookSignature.Verify(_secret, big, header));
        Assert.False(WebhookSignature.Verify(_secret, [], _good));
    }

    [Fact]
    public void The_digest_binds_the_exact_bytes_including_a_byte_order_mark_and_nul()
    {
        var withMark = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(_body).ToArray();
        Assert.False(WebhookSignature.Verify(_secret, withMark, _good));
        Assert.False(WebhookSignature.Verify(_secret, _body.Concat(new byte[] { 0 }).ToArray(), _good));
        Assert.True(WebhookSignature.Verify(_secret, withMark, WebhookSignature.Compute(_secret, withMark)));
    }

    [Fact]
    public async Task One_replayed_delivery_raced_by_many_threads_is_applied_exactly_once()
    {
        for (var round = 0; round < 50; round++)
        {
            var dedupe = new DeliveryDedupe(capacity: 8);
            var id = "delivery-" + round;
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => dedupe.TryRecord(id))));

            Assert.Equal(1, results.Count(applied => applied));
            Assert.Equal(1, dedupe.Count);
        }
    }

    [Fact]
    public async Task A_flood_of_distinct_deliveries_stays_inside_the_window()
    {
        var dedupe = new DeliveryDedupe(capacity: 100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 5_000; i++)
            {
                dedupe.TryRecord($"d-{worker}-{i}");
            }
        })));

        Assert.Equal(100, dedupe.Count);
    }
}
