using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>The <c>spec</c> column codec (doc 38, Trigger types). Pure: every spec kind round-trips and a wrong shape is rejected.</summary>
public sealed class RyuseiSpecJsonTests
{
    private static readonly DateTimeOffset _instant = new(2026, 11, 8, 14, 30, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Timer_round_trips_its_instant()
    {
        var spec = new RyuseiTimerTriggerSpec(_instant);

        var decoded = RyuseiSpecJson.Decode(RyuseiTriggerKind.Timer, RyuseiSpecJson.Encode(spec));

        Assert.Equal(spec, decoded);
    }

    [Fact]
    public void Interval_writes_an_iso_duration_and_round_trips()
    {
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromMinutes(30), _instant);

        var json = RyuseiSpecJson.Encode(spec);
        var decoded = RyuseiSpecJson.Decode(RyuseiTriggerKind.Interval, json);

        Assert.Contains("\"every\":\"PT30M\"", json, StringComparison.Ordinal);
        Assert.Equal(spec, decoded);
    }

    [Fact]
    public void Watcher_round_trips_every_optional_field()
    {
        var spec = new RyuseiWatcherTriggerSpec(
            new RyuseiPredicate
            {
                Kind = "gc.message",
                GcId = Guid.CreateVersion7(),
                AuthorAgentId = Guid.CreateVersion7(),
                PathGlob = null,
            },
            PollSeconds: 60,
            DebounceSeconds: 30,
            ExpiresAt: _instant.AddDays(2),
            MaxFires: 5);

        var decoded = RyuseiSpecJson.Decode(RyuseiTriggerKind.Watcher, RyuseiSpecJson.Encode(spec));

        Assert.Equal(spec, decoded);
    }

    [Fact]
    public void Watcher_without_bounds_omits_them_and_round_trips()
    {
        var spec = new RyuseiWatcherTriggerSpec(
            new RyuseiPredicate { Kind = "vault.changed", PathGlob = "notes/**" },
            PollSeconds: 120,
            DebounceSeconds: 10,
            ExpiresAt: null,
            MaxFires: null);

        var json = RyuseiSpecJson.Encode(spec);

        Assert.DoesNotContain("expiresAt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("maxFires", json, StringComparison.Ordinal);
        Assert.Equal(spec, RyuseiSpecJson.Decode(RyuseiTriggerKind.Watcher, json));
    }

    [Fact]
    public void Event_round_trips_its_predicate()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "pr.merged" });

        Assert.Equal(spec, RyuseiSpecJson.Decode(RyuseiTriggerKind.Event, RyuseiSpecJson.Encode(spec)));
    }

    [Fact]
    public void A_document_for_the_wrong_kind_is_rejected()
    {
        var timerJson = RyuseiSpecJson.Encode(new RyuseiTimerTriggerSpec(_instant));

        Assert.Throws<InvalidDataException>(() => RyuseiSpecJson.Decode(RyuseiTriggerKind.Interval, timerJson));
    }

    [Fact]
    public void A_spec_that_is_not_an_object_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RyuseiSpecJson.Decode(RyuseiTriggerKind.Timer, "[1,2]"));
    }

    [Fact]
    public void A_predicate_without_a_kind_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RyuseiSpecJson.Decode(RyuseiTriggerKind.Event, "{\"predicate\":{}}"));
    }
}
