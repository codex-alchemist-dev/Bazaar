using System.Text.Json;
using Bazaar.Server.Knowledge.Twin;
using Bazaar.Server.ReaderSessions;
using Bazaar.Server.Security.Readers;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Twin;

namespace Bazaar.Server.Tests.ReaderSessions;

/// <summary>The Twin prose seam running through a reader session (doc 25 WP4 on doc 15 WP5).</summary>
public sealed class ReaderTwinProseWriterTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TwinFact Fact() => new(
        "bazaar", TwinFactKinds.Dependency, "Npgsql", "1.0.0", 1.0, TwinFactStatus.Active,
        [new TwinSourceRef(TwinSourceKinds.RepoFile, "Directory.Packages.props", "abc123")], null, _at);

    private static TwinProseRequest Request() => new("bazaar", "overview", "Bazaar overview", [Fact()]);

    private static ReaderTwinProseWriter Writer(ScriptedGateway gateway, RecordingScratchStore scratch) =>
        new(
            new ReaderSessionRunner(gateway, scratch, new HandoverValidator(HandoverSchemaRegistry.Empty), HandoverSchemaRegistry.Empty),
            "agent-twin",
            "route/cheap");

    [Fact]
    public async Task Round_trip_returns_the_reply_that_the_ref_validator_then_checks()
    {
        var reply = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string> { ["text"] = "Bazaar uses Npgsql.", ["source_ref"] = "twin_fact:dependency:Npgsql" },
            new Dictionary<string, string> { ["text"] = "Invented claim.", ["source_ref"] = "twin_fact:dependency:Nothing" },
        });
        var gateway = ScriptedGateway.Replying(reply);
        var scratch = new RecordingScratchStore();

        var text = await Writer(gateway, scratch).DraftAsync(TwinProsePrompt.Build(Request()), Ct);

        var page = TwinRefValidator.Validate(Request(), TwinReplyParser.Parse(text));
        Assert.Single(page.Sentences);
        Assert.Equal(1, page.DroppedCount);
        var call = Assert.Single(gateway.Requests);
        Assert.Empty(call.Tools);
        Assert.Equal("agent-twin", call.AgentId);
        Assert.Equal("route/cheap", call.Route);
        Assert.Contains(TwinProsePrompt.FactsOpen, call.Messages[1].Text);
        Assert.Single(scratch.Purged);
    }

    [Fact]
    public async Task The_session_is_plain_text_hard_tainted_and_runs_without_a_schema()
    {
        var recorded = new List<ReaderSessionRequest>();
        var runner = new RecordingRunner(recorded);
        await new ReaderTwinProseWriter(runner, "agent-twin", "route/cheap").DraftAsync("prompt", Ct);

        var request = Assert.Single(recorded);
        Assert.Null(request.HandoverSchemaId);
        Assert.Equal(ContentTrust.External, request.Input.Trust);
        Assert.Single(request.Input.SourceRefs);
        Assert.Null(request.RequestedTools);
        Assert.Null(request.RequestedCredentials);
    }

    [Fact]
    public async Task A_gateway_failure_becomes_a_model_failure_in_the_step()
    {
        var gateway = ScriptedGateway.Events(new Bazaar.Contracts.Gateway.ModelEvent.Failure("quota_exceeded", "no"));
        var writer = Writer(gateway, new RecordingScratchStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.DraftAsync("prompt", Ct));
        Assert.Contains("quota_exceeded", ex.Message);
    }

    [Fact]
    public async Task A_refused_session_throws_and_a_blank_prompt_is_refused()
    {
        var writer = Writer(ScriptedGateway.Replying("x"), new RecordingScratchStore());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.DraftAsync("   ", Ct));
        Assert.Contains("BlankInput", ex.Message);
    }

    [Fact]
    public void Blank_agent_or_route_is_rejected_at_construction()
    {
        var runner = new RecordingRunner([]);
        Assert.Throws<ArgumentException>(() => new ReaderTwinProseWriter(runner, " ", "r"));
        Assert.Throws<ArgumentException>(() => new ReaderTwinProseWriter(runner, "a", ""));
    }

    private sealed class RecordingRunner : IReaderSessionRunner
    {
        private readonly List<ReaderSessionRequest> _seen;

        public RecordingRunner(List<ReaderSessionRequest> seen) => _seen = seen;

        public Task<ReaderSessionResult> RunAsync(ReaderSessionRequest request, CancellationToken ct)
        {
            _seen.Add(request);
            var taint = new SessionTaint(TaintState.Hard, []);
            return Task.FromResult<ReaderSessionResult>(new ReaderSessionResult.Completed(Guid.NewGuid(), taint, request.Input.SourceRefs, "ok", null));
        }
    }
}
