using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host composes the gateway's quota sink so presence hears quota facts (docs/design/23, "Event sources").</summary>
public class CompositeGatewayEventSinkTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");

    private sealed class RecordingSink : IGatewayEventSink
    {
        public List<GatewayQuotaEvent> Events { get; } = [];

        public void Publish(GatewayQuotaEvent e) => Events.Add(e);
    }

    private sealed class ThrowingSink : IGatewayEventSink
    {
        public void Publish(GatewayQuotaEvent e) => throw new InvalidOperationException("sink down");
    }

    private sealed class FakeProjector : IActivityProjector
    {
        public List<ActivityEvent> Events { get; } = [];

        public ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct)
        {
            lock (Events)
            {
                Events.Add(e);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static AgentCapacityChanged Throttled() =>
        new(_agent.ToString(), false, null, QuotaState.RateLimited);

    [Fact]
    public void With_no_sinks_nothing_is_composed()
    {
        Assert.Null(CompositeGatewayEventSink.Compose([null, null]));
    }

    [Fact]
    public void Every_sink_gets_the_event_and_a_throwing_sink_is_reported_and_skipped()
    {
        var failures = new List<Exception>();
        var recording = new RecordingSink();
        var sink = CompositeGatewayEventSink.Compose([new ThrowingSink(), recording], failures.Add);

        sink!.Publish(Throttled());

        Assert.Single(recording.Events);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task The_quota_source_adapter_forwards_the_gateway_event_to_presence()
    {
        var projector = new FakeProjector();
        var source = new QuotaPresenceSource(projector, new FakeTimeProvider());
        var sink = CompositeGatewayEventSink.Compose([new QuotaPresenceGatewayEventSink(source)]);

        sink!.Publish(Throttled());

        // The source forwards without waiting; give the fire-and-forget task a moment.
        for (var i = 0; i < 100; i++)
        {
            lock (projector.Events)
            {
                if (projector.Events.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var forwarded = Assert.Single(projector.Events);
        Assert.Equal(_agent, forwarded.AgentId);
        Assert.Equal(ActivityEventKind.QuotaThrottle, forwarded.Kind);
    }
}
