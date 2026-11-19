using Bazaar.Contracts.Kodo;
using System.Text;
using System.Text.Json;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoWebhookEnvelopeBuilderTests
{
    [Fact]
    public void BodyCarriesTypeIdTimeAndSortedIdentifiers()
    {
        KodoWebhookEnvelope envelope = KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.StateChanged,
            "evt_1",
            KodoExportTestData.Now,
            new Dictionary<string, string> { ["state"] = "degraded", ["probe_id"] = "host.disk.free" });

        using JsonDocument document = JsonDocument.Parse(envelope.Body);
        JsonElement root = document.RootElement;

        Assert.Equal(KodoWebhookEvents.StateChanged, root.GetProperty("type").GetString());
        Assert.Equal("evt_1", root.GetProperty("id").GetString());
        Assert.Equal(KodoExportTestData.Now, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(["probe_id", "state"], root.GetProperty("identifiers").EnumerateObject().Select(property => property.Name));
        Assert.Equal("host.disk.free", root.GetProperty("identifiers").GetProperty("probe_id").GetString());
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        Dictionary<string, string> identifiers = new() { ["probe_id"] = "host.disk.free" };

        KodoWebhookEnvelope first = KodoWebhookEnvelopeBuilder.Build(KodoWebhookEvents.Alert, "evt_2", KodoExportTestData.Now, identifiers);
        KodoWebhookEnvelope second = KodoWebhookEnvelopeBuilder.Build(KodoWebhookEvents.Alert, "evt_2", KodoExportTestData.Now, identifiers);

        Assert.Equal(first.Body, second.Body);
    }

    [Fact]
    public void EnvelopeKeepsItsTypeIdAndTime()
    {
        KodoWebhookEnvelope envelope = KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Tamper,
            "evt_3",
            KodoExportTestData.Now,
            new Dictionary<string, string>());

        Assert.Equal(KodoWebhookEvents.Tamper, envelope.EventType);
        Assert.Equal("evt_3", envelope.EventId);
        Assert.Equal(KodoExportTestData.Now, envelope.OccurredAt);
    }

    [Fact]
    public void EventTypesKeepTheKodoPrefixAndTheDocumentedSet()
    {
        Assert.Equal(
            [
                "kodo.state_changed",
                "kodo.alert",
                "kodo.incident.opened",
                "kodo.incident.updated",
                "kodo.incident.closed",
                "kodo.runbook.finished",
                "kodo.report.ready",
                "kodo.tamper",
            ],
            KodoWebhookEvents.All);
    }

    [Fact]
    public void RefusesAnEventTypeOutsideTheSet()
    {
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(
            "kodo.made_up",
            "evt_4",
            KodoExportTestData.Now,
            new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("evt 4")]
    [InlineData("")]
    [InlineData("evt/4")]
    public void RefusesAnEventIdThatIsNotUrlSafe(string eventId)
    {
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Alert,
            eventId,
            KodoExportTestData.Now,
            new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("ProbeId")]
    [InlineData("probe-id")]
    [InlineData("")]
    public void RefusesAnIdentifierNameThatIsNotLowercaseSnake(string name)
    {
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Alert,
            "evt_5",
            KodoExportTestData.Now,
            new Dictionary<string, string> { [name] = "value" }));
    }

    [Fact]
    public void RefusesFreeTextAsAnIdentifierValue()
    {
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Alert,
            "evt_6",
            KodoExportTestData.Now,
            new Dictionary<string, string> { ["reason"] = "the disk is full, see the log" }));
    }

    [Fact]
    public void RefusesMoreIdentifiersThanTheLimit()
    {
        Dictionary<string, string> identifiers = [];
        for (int i = 0; i <= KodoWebhookEnvelopeBuilder.MaxIdentifiers; i++)
        {
            identifiers["key_" + i] = "value";
        }

        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Alert,
            "evt_7",
            KodoExportTestData.Now,
            identifiers));
    }

    [Fact]
    public void BodyIsUtf8Json()
    {
        KodoWebhookEnvelope envelope = KodoWebhookEnvelopeBuilder.Build(
            KodoWebhookEvents.Alert,
            "evt_8",
            KodoExportTestData.Now,
            new Dictionary<string, string> { ["probe_id"] = "host.disk.free" });

        Assert.StartsWith("{\"type\":\"kodo.alert\"", Encoding.UTF8.GetString(envelope.Body), StringComparison.Ordinal);
    }
}
