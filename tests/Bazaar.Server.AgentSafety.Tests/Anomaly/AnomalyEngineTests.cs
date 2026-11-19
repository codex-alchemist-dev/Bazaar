using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Testing;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Tests.Anomaly;

public sealed class AnomalyEngineTests
{
    private static readonly AnomalyEvent _someEvent = new(AnomalyEventKind.CanaryTouched, "agent-a", "session-1", null, InScope: true, FakeClock.DefaultStart);

    [Fact]
    public async Task Every_alert_from_every_rule_is_published_in_rule_order()
    {
        var sink = new RecordingSink();
        var engine = new AnomalyEngine(
            [new FixedRule("first", Alert("first")), new FixedRule("second", Alert("second"))],
            sink,
            new CapturingLogger());

        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], sink.Published.Select(a => a.RuleId));
    }

    [Fact]
    public async Task A_throwing_rule_is_isolated_and_reported_to_the_callback()
    {
        var sink = new RecordingSink();
        var reported = new List<(string RuleId, Exception Error)>();
        var engine = new AnomalyEngine(
            [new ThrowingRule("broken"), new FixedRule("healthy", Alert("healthy"))],
            sink,
            new CapturingLogger(),
            (ruleId, error) => reported.Add((ruleId, error)));

        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);

        Assert.Equal(["healthy"], sink.Published.Select(a => a.RuleId));
        var failure = Assert.Single(reported);
        Assert.Equal("broken", failure.RuleId);
        Assert.IsType<InvalidOperationException>(failure.Error);
    }

    [Fact]
    public async Task Without_a_callback_a_throwing_rule_is_logged_not_dropped()
    {
        var logger = new CapturingLogger();
        var sink = new RecordingSink();
        var engine = new AnomalyEngine([new ThrowingRule("broken"), new FixedRule("healthy", Alert("healthy"))], sink, logger);

        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);

        Assert.Equal(["healthy"], sink.Published.Select(a => a.RuleId));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("broken", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task A_failing_sink_is_isolated_per_alert()
    {
        var sink = new RecordingSink { FailFirstPublish = true };
        var reported = new List<string>();
        var engine = new AnomalyEngine(
            [new FixedRule("multi", Alert("one"), Alert("two"))],
            sink,
            new CapturingLogger(),
            (ruleId, _) => reported.Add(ruleId));

        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);

        Assert.Equal(["two"], sink.Published.Select(a => a.Detail));
        Assert.Equal(["multi"], reported);
    }

    [Fact]
    public async Task A_rule_that_throws_on_a_later_event_does_not_stop_the_engine()
    {
        var sink = new RecordingSink();
        var rule = new FlakyRule();
        var engine = new AnomalyEngine([rule], sink, new CapturingLogger());

        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);
        await engine.HandleAsync(_someEvent, TestContext.Current.CancellationToken);

        Assert.Equal(["second"], sink.Published.Select(a => a.Detail));
    }

    private static AnomalyAlert Alert(string detail) =>
        new("fixed", "agent-a", "session-1", AnomalySeverity.Low, FakeClock.DefaultStart, detail);

    private sealed class FixedRule(string id, params AnomalyAlert[] alerts) : IAnomalyRule
    {
        public string Id { get; } = id;

        public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e) => alerts.Select(a => a with { RuleId = Id }).ToList();
    }

    private sealed class ThrowingRule(string id) : IAnomalyRule
    {
        public string Id { get; } = id;

        public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e) => throw new InvalidOperationException("rule broke");
    }

    private sealed class FlakyRule : IAnomalyRule
    {
        private int _calls;

        public string Id => "flaky";

        public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e)
        {
            _calls++;
            if (_calls == 1)
            {
                throw new InvalidOperationException("first call fails");
            }

            return [Alert("second")];
        }
    }

    private sealed class RecordingSink : IAnomalyAlertSink
    {
        private readonly List<AnomalyAlert> _published = [];

        private int _attempts;

        public bool FailFirstPublish { get; init; }

        public IReadOnlyList<AnomalyAlert> Published => _published;

        public ValueTask PublishAsync(AnomalyAlert alert, CancellationToken ct)
        {
            if (FailFirstPublish && _attempts++ == 0)
            {
                throw new InvalidOperationException("sink broke");
            }

            _published.Add(alert);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<AnomalyEngine>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
