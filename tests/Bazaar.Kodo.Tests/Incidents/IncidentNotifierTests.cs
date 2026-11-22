using Bazaar.Kodo.Incidents;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentNotifierTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task OpeningNotifiesThroughTheResponderChannelsWithIdentifiersOnly()
    {
        IncidentHarness h = new(withNotifier: true);
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.AutoOpen(), _ct);

        AlertRouteResult notice = result.Notification!;
        Assert.Equal(AlertDecision.Delivered, notice.Decision);
        Assert.Equal(
            [AlertChannelKind.InClient, AlertChannelKind.Push, AlertChannelKind.Email, AlertChannelKind.Webhook],
            h.Channels.Where(c => c.Sent.Count > 0).Select(c => c.Kind).Order());

        AlertDelivery webhook = Assert.Single(h.Channels.Single(c => c.Kind == AlertChannelKind.Webhook).Sent);
        Assert.Equal(AlertSeverity.Critical, webhook.Severity);
        Assert.Equal("inc-000001", webhook.Payload.Identifiers["incident_id"]);
        Assert.Equal("frozen", webhook.Payload.Identifiers["incident_phase"]);
        Assert.DoesNotContain(webhook.Payload.Identifiers.Values, value => value.Contains("Integrity", StringComparison.Ordinal));

        AlertDelivery push = Assert.Single(h.Channels.Single(c => c.Kind == AlertChannelKind.Push).Sent);
        Assert.Equal(["alert_id"], push.Payload.Identifiers.Keys);
    }

    [Fact]
    public async Task ARepeatNotificationInsideTheDedupeWindowIsSuppressedByTheRouter()
    {
        IncidentHarness h = new(withNotifier: true);
        IncidentRecord record = (await h.Controller().OpenAsync(IncidentHarness.AutoOpen(), _ct)).Incident!;
        int sent = h.Channels.Sum(c => c.Sent.Count);

        AlertRouteResult again = await h.Notifier!.NotifyAsync(record, IncidentNotice.Opened, _ct);

        Assert.Equal(AlertDecision.Suppressed, again.Decision);
        Assert.Equal("duplicate", again.Reason);
        Assert.Equal(sent, h.Channels.Sum(c => c.Sent.Count));

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(AlertDecision.Delivered, (await h.Notifier.NotifyAsync(record, IncidentNotice.Opened, _ct)).Decision);
    }

    [Fact]
    public async Task ResolvingSendsOneResolutionAndAClosedNoticeFollows()
    {
        IncidentHarness h = new(withNotifier: true);
        IncidentController controller = h.Controller();
        h.ApproveAll();
        IncidentRecord record = await h.WalkAsync(controller, IncidentPhase.Investigating);

        IncidentResult resolved = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Resolved, green: h.Clock.GetUtcNow() - TimeSpan.FromHours(1)), _ct);
        Assert.Equal(AlertDecision.Delivered, resolved.Notification!.Decision);
        Assert.Equal("incident_resolved", h.Notifier!.Build(resolved.Incident!, IncidentNotice.Resolved).Rule);
        Assert.True(h.Notifier.Build(resolved.Incident!, IncidentNotice.Resolved).IsResolution);

        IncidentResult closed = await controller.AdvanceAsync(
            IncidentHarness.Advance(record.Id, IncidentPhase.Closed, IncidentHarness.Controls(KodoControls.IncidentExit)), _ct);
        Assert.True(closed.Ok);
        Assert.Equal(AlertSeverity.Info, h.Notifier.Build(closed.Incident!, IncidentNotice.Closed).Severity);
        Assert.NotNull(closed.Notification);
    }

    [Fact]
    public async Task ABrokenChannelNeverUndoesTheIncident()
    {
        IncidentHarness h = new(withNotifier: true);
        h.Channels.Clear();
        AlertDispatcher empty = new(h.Router, []);
        IncidentNotifier notifier = new(h.Router, empty, h.Clock);
        IncidentController controller = new(h.Store, h.Journal, h.Freeze, h.Gate, h.Options, h.Clock, notifier: notifier);

        IncidentResult result = await controller.OpenAsync(IncidentHarness.AutoOpen(), _ct);

        Assert.True(result.Ok);
        Assert.Equal(IncidentPhase.Frozen, result.Incident!.Phase);
    }

    [Fact]
    public async Task WithoutANotifierNothingIsSent()
    {
        IncidentHarness h = new();
        IncidentResult result = await h.Controller().OpenAsync(IncidentHarness.AutoOpen(), _ct);

        Assert.True(result.Ok);
        Assert.Null(result.Notification);
        Assert.All(h.Channels, channel => Assert.Empty(channel.Sent));
    }

    [Fact]
    public void TheAlertIsAlwaysAcceptableToTheRouter()
    {
        IncidentHarness h = new(withNotifier: true);
        IncidentRecord record = new(
            1, 7, "inc-000007", AlertSeverity.Warning, IncidentTrigger.Manual, "host.disk.free:data", "t", "owner", "r", DateTimeOffset.UnixEpoch,
            IncidentPhase.Frozen, [], [], [], [], [], false, false, null, 0, DateTimeOffset.UnixEpoch, 0);

        foreach (IncidentNotice notice in Enum.GetValues<IncidentNotice>())
        {
            Assert.Null(h.Notifier!.Build(record, notice).Problem());
        }
    }
}
