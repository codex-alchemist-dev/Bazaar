using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Secrets;
using Bazaar.Server.Tests.Gateway.Pipeline;

namespace Bazaar.Server.Tests.Gateway.Secrets;

/// <summary>Planted-key tests: a resolved key must not survive into any failure event the gateway yields.</summary>
public class PlantedKeyGatewayTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    // Characters that change under URL and JSON encoding, so each encoded echo is distinct from the raw key.
    private const string _plantedKey = "PLANTED/key+with=odd \"chars\"\\";

    [Fact]
    public async Task Key_echoed_by_a_fake_provider_failure_is_absent_from_the_failure_event()
    {
        var h = new GatewayHarness();
        h.Secrets.Values[GatewayHarness.SecretRef("a")] = _plantedKey;
        h.Adapter("a").Provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.Failure("bad_request", $"Invalid key {_plantedKey} for this route.")));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(events));
        Assert.Equal("bad_request", failure.Code);
        Assert.DoesNotContain(_plantedKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(SecretScrubber.Marker, failure.Message, StringComparison.Ordinal);
        Assert.Empty(h.Adapter("b").Provider.Calls);
    }

    [Fact]
    public async Task Url_and_json_encoded_echoes_of_the_key_are_absent_from_failure_events()
    {
        var h = new GatewayHarness();
        h.Secrets.Values[GatewayHarness.SecretRef("a")] = _plantedKey;
        var urlEncoded = Uri.EscapeDataString(_plantedKey);
        var jsonEncoded = System.Text.Json.JsonSerializer.Serialize(_plantedKey)[1..^1];
        h.Adapter("a").Provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.Failure("bad_request", $"url {urlEncoded} json {jsonEncoded}")));

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(events));
        Assert.DoesNotContain(urlEncoded, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(jsonEncoded, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_echoed_by_a_retryable_failure_is_absent_from_the_events_and_the_quota_report()
    {
        var h = new GatewayHarness();
        h.Secrets.Values[GatewayHarness.SecretRef("a")] = _plantedKey;
        h.Adapter("a").Provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.Failure("provider_error", $"upstream echoed {_plantedKey}")));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal(["TextDelta", "UsageReported", "Done"], events.Select(e => e.GetType().Name));
        Assert.DoesNotContain(_plantedKey, string.Join('\n', events.Select(e => e.ToString())), StringComparison.Ordinal);
        var report = Assert.Single(h.Quota.Reports);
        Assert.DoesNotContain(_plantedKey, report.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(h.InternalErrors);
    }
}
