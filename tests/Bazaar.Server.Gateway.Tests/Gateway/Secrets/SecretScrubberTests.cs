using System.Text.RegularExpressions;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Secrets;

namespace Bazaar.Server.Tests.Gateway.Secrets;

public class SecretScrubberTests
{
    // Contains characters that change under URL and JSON encoding, so every encoded form differs from the raw value.
    private const string _key = "sk-live/abc+def=ghi \"quoted\"\\x";

    [Fact]
    public void Plain_text_key_is_replaced_by_the_marker()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);

        var text = $"Authorization: Bearer {_key} was rejected";

        Assert.Equal($"Authorization: Bearer {SecretScrubber.Marker} was rejected", scrubber.Scrub(text));
    }

    [Fact]
    public void Percent_encoded_key_is_replaced_in_either_hex_case()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);
        var upper = Uri.EscapeDataString(_key);
        var lower = Regex.Replace(upper, "%[0-9A-F]{2}", m => m.Value.ToLowerInvariant());

        Assert.NotEqual(upper, lower);
        Assert.Equal($"https://api.example/v1?key={SecretScrubber.Marker}", scrubber.Scrub($"https://api.example/v1?key={upper}"));
        Assert.Equal($"https://api.example/v1?key={SecretScrubber.Marker}", scrubber.Scrub($"https://api.example/v1?key={lower}"));
    }

    [Fact]
    public void JSON_escaped_key_is_replaced()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);
        var jsonEscaped = System.Text.Json.JsonSerializer.Serialize(_key)[1..^1];

        Assert.Equal($"{{\"error\":\"{SecretScrubber.Marker}\"}}", scrubber.Scrub($"{{\"error\":\"{jsonEscaped}\"}}"));
    }

    [Fact]
    public void Key_echoed_inside_an_upstream_error_body_is_replaced()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);
        var body = $"{{\"error\":{{\"type\":\"invalid_request_error\",\"message\":\"Incorrect API key provided: {_key}. You can find your key at https://example.test/keys\"}}}}";

        var scrubbed = scrubber.Scrub(body);

        Assert.DoesNotContain("sk-live", scrubbed, StringComparison.Ordinal);
        Assert.Contains(SecretScrubber.Marker, scrubbed, StringComparison.Ordinal);
        Assert.Contains("invalid_request_error", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_without_a_tracked_value_comes_back_as_the_same_instance()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);
        var text = "The upstream returned HTTP 500.";

        Assert.Same(text, scrubber.Scrub(text));
    }

    [Fact]
    public void Nothing_is_replaced_before_any_value_is_tracked()
    {
        var scrubber = new SecretScrubber();
        const string text = "anything at all";

        Assert.Same(text, scrubber.Scrub(text));
    }

    [Fact]
    public void Marker_is_not_scanned_again_when_a_short_value_matches_inside_it()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track("a");

        Assert.Equal(SecretScrubber.Marker, scrubber.Scrub("a"));
    }

    [Fact]
    public void Tracking_the_same_value_twice_counts_once_and_keeps_it()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);
        scrubber.Track(_key);

        Assert.Equal(1, scrubber.TrackedCount);
        Assert.Contains(SecretScrubber.Marker, scrubber.Scrub(_key), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_value_is_refused()
    {
        var scrubber = new SecretScrubber();

        Assert.Throws<ArgumentException>(() => scrubber.Track(string.Empty));
    }

    [Fact]
    public void Failure_and_warning_text_are_scrubbed_and_other_fields_kept()
    {
        var scrubber = new SecretScrubber();
        scrubber.Track(_key);

        var failure = scrubber.ScrubEvent(new ModelEvent.Failure("rate_limited", $"echoed {_key}", 42));
        var warning = scrubber.ScrubEvent(new ModelEvent.Warning("note", $"echoed {_key}"));
        var done = new ModelEvent.Done(StopReason.EndTurn, "m");

        Assert.Equal(new ModelEvent.Failure("rate_limited", $"echoed {SecretScrubber.Marker}", 42), failure);
        Assert.Equal(new ModelEvent.Warning("note", $"echoed {SecretScrubber.Marker}"), warning);
        Assert.Same(done, scrubber.ScrubEvent(done));
    }

    [Fact]
    public async Task Concurrent_tracking_and_scrubbing_keep_every_value()
    {
        var scrubber = new SecretScrubber();
        var values = Enumerable.Range(0, 200).Select(i => $"planted-{i:D4}/value+{i}").ToArray();

        await Parallel.ForEachAsync(values, TestContext.Current.CancellationToken, (value, _) =>
        {
            scrubber.Track(value);
            scrubber.Scrub($"upstream said {value}");
            return ValueTask.CompletedTask;
        });

        Assert.Equal(values.Length, scrubber.TrackedCount);
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, scrubber.Scrub($"again {value}"), StringComparison.Ordinal);
        }
    }
}
