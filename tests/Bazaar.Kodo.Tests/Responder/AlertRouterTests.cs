using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Responder;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class AlertRouterTests
{
    private static KodoAlert Alert(
        string fingerprint = "host.disk.free:data",
        string category = "host",
        AlertSeverity severity = AlertSeverity.Warning,
        bool resolution = false) =>
        new(fingerprint, category, severity, "disk_low", new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), new Dictionary<string, string>(), resolution);

    private static (AlertRouter Router, FakeTimeProvider Clock) Make(AlertOptions? options = null)
    {
        FakeTimeProvider clock = ResponderTestKit.Clock();
        return (new AlertRouter(options ?? new AlertOptions(), clock), clock);
    }

    [Theory]
    [InlineData(KodoState.Healthy, KodoState.Degraded, AlertSeverity.Warning)]
    [InlineData(KodoState.Healthy, KodoState.Failing, AlertSeverity.Critical)]
    [InlineData(KodoState.Degraded, KodoState.Failing, AlertSeverity.Critical)]
    [InlineData(KodoState.Failing, KodoState.Degraded, AlertSeverity.Warning)]
    [InlineData(KodoState.Failing, KodoState.Recovering, AlertSeverity.Info)]
    [InlineData(KodoState.Recovering, KodoState.Healthy, AlertSeverity.Info)]
    [InlineData(KodoState.Failing, KodoState.Frozen, AlertSeverity.Critical)]
    public void SeverityComesFromTheStateMovedInto(KodoState from, KodoState to, AlertSeverity expected)
    {
        Assert.Equal(expected, KodoAlerts.SeverityForTransition(from, to));
        Assert.Equal(expected, KodoAlerts.FromStateChange(from, to, DateTimeOffset.UnixEpoch)!.Severity);
    }

    [Fact]
    public void NoStateChangeMakesNoAlertAndMovingToHealthyIsAResolution()
    {
        Assert.Null(KodoAlerts.SeverityForTransition(KodoState.Failing, KodoState.Failing));
        Assert.Null(KodoAlerts.FromStateChange(KodoState.Failing, KodoState.Failing, DateTimeOffset.UnixEpoch));
        Assert.True(KodoAlerts.FromStateChange(KodoState.Recovering, KodoState.Healthy, DateTimeOffset.UnixEpoch)!.IsResolution);
        Assert.False(KodoAlerts.FromStateChange(KodoState.Healthy, KodoState.Failing, DateTimeOffset.UnixEpoch)!.IsResolution);
    }

    [Fact]
    public void FlapAlertIsRaisedOnlyWhenTheEvaluatorSaysSo()
    {
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], true);
        KodoEvaluation yes = new(KodoState.Degraded, KodoState.Degraded, true, true, memory);
        KodoEvaluation no = yes with { RaiseFlapAlert = false };

        Assert.Equal("flap_detected", KodoAlerts.FromEvaluation(yes, DateTimeOffset.UnixEpoch)!.Rule);
        Assert.Null(KodoAlerts.FromEvaluation(no, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void DuplicateInsideTheWindowIsSuppressedAndTheBoundaryDelivers()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert()).Decision);

        clock.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        AlertRouteResult dup = router.Route(Alert());
        Assert.Equal(AlertDecision.Suppressed, dup.Decision);
        Assert.Equal("duplicate", dup.Reason);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert()).Decision);
    }

    [Fact]
    public void ChangedSeverityOfTheSameFingerprintIsNewsAndDelivers()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });

        router.Route(Alert(severity: AlertSeverity.Warning));

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert(severity: AlertSeverity.Critical)).Decision);
    }

    [Fact]
    public void DifferentFingerprintsAreNotDuplicates()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });

        router.Route(Alert("host.disk.free:data"));

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.disk.free:logs")).Decision);
    }

    [Fact]
    public void BurstInOneCategoryIsGroupedAndFlushesOneSummary()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make();

        AlertRouteResult leader = router.Route(Alert("host.a:x"));
        clock.Advance(TimeSpan.FromSeconds(59));
        AlertRouteResult second = router.Route(Alert("host.b:x"));
        AlertRouteResult third = router.Route(Alert("host.c:x"));

        Assert.Equal(AlertDecision.Delivered, leader.Decision);
        Assert.Equal("grouped", second.Reason);
        Assert.Equal("grouped", third.Reason);
        Assert.Empty(router.FlushGroups());

        clock.Advance(TimeSpan.FromSeconds(1));
        IReadOnlyList<AlertRouteResult> summaries = router.FlushGroups();
        AlertRouteResult summary = Assert.Single(summaries);
        Assert.Equal(AlertDecision.Delivered, summary.Decision);
        Assert.Equal("3", summary.Deliveries[0].Payload.Identifiers["count"]);
        Assert.Empty(router.FlushGroups());
    }

    [Fact]
    public void GroupWindowEndsAtTheBoundaryAndOtherCategoriesAreNotGrouped()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make();

        router.Route(Alert("host.a:x"));
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("db.a:x", category: "db")).Decision);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.b:x")).Decision);
    }

    [Fact]
    public void HigherSeverityInsideAGroupStillDelivers()
    {
        (AlertRouter router, _) = Make();

        router.Route(Alert("host.a:x", severity: AlertSeverity.Warning));

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.b:x", severity: AlertSeverity.Critical)).Decision);
    }

    [Fact]
    public void RateLimitStopsTheSeventhWarningOnAChannelAndRecoversAfterTheWindow()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(AlertDecision.Delivered, router.Route(Alert($"host.p{i}:x")).Decision);
        }

        AlertRouteResult limited = router.Route(Alert("host.p6:x"));
        Assert.Equal(AlertDecision.Suppressed, limited.Decision);
        Assert.Equal("rate_limited", limited.Reason);
        Assert.All(limited.Suppressions, s => Assert.Equal("rate_limited", s.Reason));

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(AlertDecision.Suppressed, router.Route(Alert("host.p7:x")).Decision);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.p8:x")).Decision);
    }

    [Fact]
    public void CriticalAlertsKeepTheirOwnLargerBudget()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });

        for (int i = 0; i < 6; i++)
        {
            router.Route(Alert($"host.p{i}:x"));
        }

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.c:x", severity: AlertSeverity.Critical)).Decision);
    }

    [Fact]
    public void QuietWindowKeepsWarningsInTheClientButNeverQuietsCritical()
    {
        QuietWindow night = new(22 * 60, 6 * 60);
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions { Quiet = night, GroupWindow = TimeSpan.Zero });
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero));

        AlertRouteResult warning = router.Route(Alert());
        AlertRouteResult critical = router.Route(Alert("host.x:y", severity: AlertSeverity.Critical));

        Assert.Equal([AlertChannelKind.InClient], warning.Deliveries.Select(d => d.Channel));
        Assert.Contains(warning.Suppressions, s => s is { Channel: AlertChannelKind.Webhook, Reason: "quiet" });
        Assert.Contains(AlertChannelKind.Push, critical.Deliveries.Select(d => d.Channel));
        Assert.Empty(critical.Suppressions);
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    public void QuietWindowWrapsPastMidnightWithAHalfOpenEnd(int hour, int minute, bool quiet) =>
        Assert.Equal(quiet, new QuietWindow(22 * 60, 6 * 60).Contains(new DateTimeOffset(2026, 11, 19, hour, minute, 0, TimeSpan.Zero)));

    [Fact]
    public void ShippedDefaultsHaveNoQuietWindow()
    {
        Assert.Null(new AlertOptions().Quiet);
        Assert.False(new QuietWindow(5, 5).Contains(DateTimeOffset.UnixEpoch.AddMinutes(5)));
    }

    [Fact]
    public void PushIsContentFreeAndEmailCarriesOnlySeverityRuleAndId()
    {
        (AlertRouter router, _) = Make();

        AlertRouteResult result = router.Route(Alert(severity: AlertSeverity.Critical) with
        {
            Identifiers = new Dictionary<string, string> { ["probe"] = "host.disk.free" },
        });

        AlertDelivery push = result.Deliveries.Single(d => d.Channel == AlertChannelKind.Push);
        AlertDelivery email = result.Deliveries.Single(d => d.Channel == AlertChannelKind.Email);
        AlertDelivery web = result.Deliveries.Single(d => d.Channel == AlertChannelKind.Webhook);
        Assert.Equal(["alert_id"], push.Payload.Identifiers.Keys);
        Assert.Equal(["alert_id", "rule", "severity"], email.Payload.Identifiers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("host.disk.free", web.Payload.Identifiers["probe"]);
        Assert.Equal("owner", push.Target);
    }

    [Fact]
    public void WebhookPayloadBuildsAnEnvelopeThroughTheExistingBuilder()
    {
        (AlertRouter router, _) = Make();
        AlertDelivery web = router.Route(Alert(severity: AlertSeverity.Critical)).Deliveries.Single(d => d.Channel == AlertChannelKind.Webhook);

        Bazaar.Kodo.Export.KodoWebhookEnvelope envelope = web.Payload.ToWebhookEnvelope();

        Assert.Equal("kodo.alert", envelope.EventType);
        Assert.Contains("\"rule\":\"disk_low\"", System.Text.Encoding.UTF8.GetString(envelope.Body));
    }

    [Fact]
    public void UnacknowledgedCriticalEscalatesAtTheWindowToTheNextRoleThenTheOwnerDevices()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions
        {
            CategoryOwners = new Dictionary<string, string> { ["db"] = "dba" },
        });
        AlertRouteResult first = router.Route(Alert("db.x:y", "db", AlertSeverity.Critical));
        Assert.Equal("dba", first.Deliveries[0].Target);

        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        Assert.Empty(router.Escalate());

        clock.Advance(TimeSpan.FromSeconds(1));
        AlertEscalation toOwner = Assert.Single(router.Escalate());
        Assert.Equal(("dba", "owner"), (toOwner.FromTarget, toOwner.ToTarget));
        Assert.All(toOwner.Deliveries, d => Assert.Equal(1, d.EscalationLevel));
        Assert.Equal($"{first.AlertId}-e1", toOwner.Deliveries[0].Payload.EventId);

        clock.Advance(TimeSpan.FromMinutes(15));
        AlertEscalation toDevices = Assert.Single(router.Escalate());
        Assert.Equal("owner-devices", toDevices.ToTarget);

        clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Empty(router.Escalate());
    }

    [Fact]
    public void AcknowledgementAndResolutionStopEscalation()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });
        router.Route(Alert("host.a:x", severity: AlertSeverity.Critical));
        router.Route(Alert("host.b:x", severity: AlertSeverity.Critical));
        Assert.Equal(2, router.PendingAcknowledgements);

        Assert.True(router.Acknowledge("host.a:x"));
        Assert.False(router.Acknowledge("host.a:x"));
        AlertRouteResult resolved = router.Route(Alert("host.b:x", severity: AlertSeverity.Info, resolution: true));
        Assert.Equal(AlertDecision.Delivered, resolved.Decision);
        Assert.Equal("true", resolved.Deliveries[0].Payload.Identifiers["resolved"]);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(router.Escalate());
    }

    [Fact]
    public void ResolutionOfSomethingNeverRaisedIsSuppressed()
    {
        (AlertRouter router, _) = Make();

        Assert.Equal("nothing_to_resolve", router.Route(Alert(severity: AlertSeverity.Info, resolution: true)).Reason);
    }

    [Fact]
    public void ResolutionClearsDedupeSoARecurrenceDeliversAgain()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });
        router.Route(Alert());
        router.Route(Alert(severity: AlertSeverity.Info, resolution: true));

        Assert.Equal(AlertDecision.Delivered, router.Route(Alert()).Decision);
    }

    [Fact]
    public void BreakerOpensAfterConsecutiveFailuresAndTheChannelIsSkippedUntilTheCooldown()
    {
        (AlertRouter router, FakeTimeProvider clock) = Make(new AlertOptions
        {
            GroupWindow = TimeSpan.Zero,
            DedupeWindow = TimeSpan.FromSeconds(1),
            BreakerFailures = 2,
            MaxPerChannel = 50,
            MaxCriticalPerChannel = 50,
        });

        for (int i = 0; i < 2; i++)
        {
            AlertDelivery web = router.Route(Alert($"host.f{i}:x", severity: AlertSeverity.Warning)).Deliveries.Single(d => d.Channel == AlertChannelKind.Webhook);
            router.RecordResult(web, failed: true);
        }

        Assert.Equal(BreakerState.Open, router.BreakerStateOf(AlertChannelKind.Webhook));
        AlertRouteResult skipped = router.Route(Alert("host.f9:x"));
        Assert.Contains(skipped.Suppressions, s => s is { Channel: AlertChannelKind.Webhook, Reason: "breaker_open" });
        Assert.Contains(skipped.Deliveries, d => d.Channel == AlertChannelKind.InClient);

        clock.Advance(TimeSpan.FromSeconds(60));
        AlertRouteResult trial = router.Route(Alert("host.f10:x"));
        Assert.Contains(trial.Deliveries, d => d.Channel == AlertChannelKind.Webhook);
    }

    [Fact]
    public void InvalidAlertsAreRejectedNotTrimmedAndNothingIsTracked()
    {
        (AlertRouter router, _) = Make();

        Assert.Equal("invalid_fingerprint", router.Route(Alert("has space")).Reason);
        Assert.Equal("invalid_category", router.Route(Alert(category: "Host")).Reason);
        Assert.Equal("invalid_rule", router.Route(Alert() with { Rule = "free text rule" }).Reason);
        Assert.Equal("invalid_severity", router.Route(Alert() with { Severity = (AlertSeverity)9 }).Reason);
        Assert.Equal("invalid_identifiers", router.Route(Alert() with { Identifiers = new Dictionary<string, string> { ["note"] = "a free text note" } }).Reason);
        Assert.Equal("invalid_identifiers", router.Route(Alert() with { Identifiers = new Dictionary<string, string> { ["alert_id"] = "spoof" } }).Reason);
        Assert.Equal(
            "invalid_identifiers",
            router.Route(Alert() with { Identifiers = Enumerable.Range(0, 9).ToDictionary(i => $"k{i}", _ => "v") }).Reason);
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert()).Decision);
    }

    [Fact]
    public void BadOptionsAreRejectedWhenTheRouterIsBuilt()
    {
        FakeTimeProvider clock = ResponderTestKit.Clock();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertRouter(new AlertOptions { MaxPerChannel = 0 }, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertRouter(new AlertOptions { CriticalEscalation = TimeSpan.FromSeconds(10) }, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertRouter(new AlertOptions { Quiet = new QuietWindow(-1, 5) }, clock));
        Assert.Throws<ArgumentException>(() => new AlertRouter(
            new AlertOptions
            {
                Channels = new Dictionary<AlertSeverity, IReadOnlyList<AlertChannelKind>>
                {
                    [AlertSeverity.Info] = [AlertChannelKind.InClient],
                    [AlertSeverity.Warning] = [AlertChannelKind.InClient],
                    [AlertSeverity.Critical] = [AlertChannelKind.Email],
                },
            },
            clock));
    }

    [Fact]
    public void FullTrackingTableDeliversInsteadOfSuppressing()
    {
        (AlertRouter router, _) = Make(new AlertOptions { MaxTracked = 1, GroupWindow = TimeSpan.Zero });

        router.Route(Alert("host.a:x"));
        router.Route(Alert("host.b:x"));

        // b was not tracked, so a repeat of b is delivered rather than lost; a repeat of a is still a duplicate.
        Assert.Equal(AlertDecision.Delivered, router.Route(Alert("host.b:x")).Decision);
        Assert.Equal("duplicate", router.Route(Alert("host.a:x")).Reason);
    }

    [Fact]
    public async Task DispatcherSendsThroughChannelsAndFeedsTheBreaker()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero, BreakerFailures = 1 });
        FakeChannel inClient = new(AlertChannelKind.InClient, AlertSendOutcome.Sent);
        FakeChannel webhook = new(AlertChannelKind.Webhook, AlertSendOutcome.Failed);
        AlertDispatcher dispatcher = new(router, [inClient, webhook]);

        AlertRouteResult routed = router.Route(Alert());
        IReadOnlyList<(AlertDelivery Delivery, AlertSendOutcome Outcome)> outcomes = await dispatcher.DispatchAsync(routed.Deliveries, CancellationToken.None);

        Assert.Equal([AlertSendOutcome.Sent, AlertSendOutcome.Failed], outcomes.Select(o => o.Outcome));
        Assert.Equal(1, inClient.Sent);
        Assert.Equal(BreakerState.Open, router.BreakerStateOf(AlertChannelKind.Webhook));
        Assert.Equal(BreakerState.Closed, router.BreakerStateOf(AlertChannelKind.InClient));
    }

    [Fact]
    public async Task DispatcherCountsAMissingChannelAndAThrowingChannelAsFailures()
    {
        (AlertRouter router, _) = Make(new AlertOptions { GroupWindow = TimeSpan.Zero });
        AlertDispatcher dispatcher = new(router, [new ThrowingChannel()]);

        AlertRouteResult routed = router.Route(Alert());
        IReadOnlyList<(AlertDelivery Delivery, AlertSendOutcome Outcome)> outcomes = await dispatcher.DispatchAsync(routed.Deliveries, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(AlertSendOutcome.Failed, o.Outcome));
    }

    private sealed class FakeChannel(AlertChannelKind kind, AlertSendOutcome outcome) : IAlertChannel
    {
        public AlertChannelKind Kind { get; } = kind;

        public int Sent { get; private set; }

        public ValueTask<AlertSendOutcome> SendAsync(AlertDelivery delivery, CancellationToken cancellationToken)
        {
            Sent++;
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingChannel : IAlertChannel
    {
        public AlertChannelKind Kind => AlertChannelKind.InClient;

        public ValueTask<AlertSendOutcome> SendAsync(AlertDelivery delivery, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("down");
    }
}
