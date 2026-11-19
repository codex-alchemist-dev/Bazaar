using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Memory.Consolidation;
using Bazaar.Server.Memory.Consolidation.Fakes;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Server.Tests.Memory.Consolidation.ConsolidationTestSupport;

namespace Bazaar.Server.Tests.Memory.Consolidation;

public class ConsolidationRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Describe(ConsolidationReport r) =>
        string.Join("|", r.Status, r.EpisodeCount, r.QuarantinedExcluded, r.AcceptedCount, r.Rejected.Count,
            r.MaintenanceReport, r.CommitSha, r.CommitMessage, r.TokensUsed, r.TokensEstimated, r.Detail);

    [Fact]
    public async Task Replayed_script_gives_identical_report_and_exactly_one_commit()
    {
        RecordingBatchSink First() => new();
        ConsolidationReport Run(RecordingBatchSink sink)
        {
            var provider = new FakeProvider();
            provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"), CreateSemantic("retries"))), Usage(120, 80), Done());
            var episodes = new InMemoryEpisodeSource();
            episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
            episodes.Add(Agent, Episode("01J9ZEPISODE00000000000002"));
            var time = new FakeTimeProvider(Start);
            return Runner(new ScriptedGateway(provider), episodes, sink, time)
                .RunAsync(Agent, new ConsolidationOptions(), Ct).GetAwaiter().GetResult();
        }

        var sinkA = First();
        var sinkB = First();
        var a = Run(sinkA);
        var b = Run(sinkB);

        Assert.Equal(ConsolidationStatus.Completed, a.Status);
        Assert.Equal(2, a.AcceptedCount);
        Assert.Equal("abc123", a.CommitSha);
        Assert.Equal(Describe(a), Describe(b));
        Assert.Single(sinkA.Commits);
        Assert.Single(sinkB.Commits);
        Assert.Equal(sinkA.Commits[0].Items.Select(i => (i.WriteId, i.Path, i.Content)),
            sinkB.Commits[0].Items.Select(i => (i.WriteId, i.Path, i.Content)));
    }

    [Fact]
    public async Task Commit_message_comes_from_MemoryCommitMessage_and_notes_carry_derived_from()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(report.CommitMessage, Bazaar.Server.Memory.Write.MemoryCommitMessage.Build(sink.Commits[0]));
        Assert.Contains("Bazaar-Memory-Ops: 1", report.CommitMessage);
        Assert.Contains("derived_from", sink.Commits[0].Items[0].Content);
        Assert.Contains("01J9ZEPISODE00000000000001", sink.Commits[0].Items[0].Content);
        Assert.Equal($"agents/{Agent}/memory/semantic/backoff.md", sink.Commits[0].Items[0].Path);
    }

    [Fact]
    public async Task Second_concurrent_run_is_skipped_locked_and_makes_no_model_call()
    {
        var locks = new InMemoryConsolidationLock();
        using var held = await locks.TryAcquireAsync(Agent, Ct);
        Assert.NotNull(held);

        var provider = new FakeProvider();
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start), locks)
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.SkippedLocked, report.Status);
        Assert.Empty(provider.Calls);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Lock_is_released_after_a_run()
    {
        var locks = new InMemoryConsolidationLock();
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply()), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));

        await Runner(new ScriptedGateway(provider), episodes, new RecordingBatchSink(), new FakeTimeProvider(Start), locks)
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        using var again = await locks.TryAcquireAsync(Agent, Ct);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Time_cap_stops_the_run_when_the_gateway_advances_the_clock()
    {
        var time = new FakeTimeProvider(Start);
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Usage(10, 10), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();
        var gateway = new ScriptedGateway(provider, time, TimeSpan.FromMinutes(6));

        var report = await Runner(gateway, episodes, sink, time)
            .RunAsync(Agent, new ConsolidationOptions(MaxMinutes: 10), Ct);

        Assert.Equal(ConsolidationStatus.CapExceeded, report.Status);
        Assert.Empty(sink.Commits);
        Assert.Null(report.CommitSha);
    }

    [Fact]
    public async Task Token_cap_stops_the_run_on_reported_usage()
    {
        var provider = new FakeProvider();
        // The cap is above the character estimate of the prompt and reply, so only the reported usage trips it.
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Usage(900, 200), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(MaxTokens: 1000), Ct);

        Assert.Equal(ConsolidationStatus.CapExceeded, report.Status);
        Assert.Equal(1100, report.TokensUsed);
        Assert.False(report.TokensEstimated);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Without_usage_tokens_are_estimated_from_characters()
    {
        var reply = Reply(CreateSemantic("backoff"));
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(reply), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));

        var report = await Runner(new ScriptedGateway(provider), episodes, new RecordingBatchSink(), new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.True(report.TokensEstimated);
        Assert.True(report.TokensUsed > 0);
        Assert.Equal(ConsolidationStatus.Completed, report.Status);
    }

    [Fact]
    public async Task Malformed_json_gives_malformed_output_and_no_commit()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text("here is my answer: {not json"), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.MalformedOutput, report.Status);
        Assert.Empty(sink.Commits);
        Assert.Equal(0, report.AcceptedCount);
    }

    [Fact]
    public async Task Malformed_output_keeps_episodes_for_the_next_run()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text("[]"), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));

        await Runner(new ScriptedGateway(provider), episodes, new RecordingBatchSink(), new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Single(await episodes.ReadSinceLastRunAsync(Agent, Ct));
    }

    [Fact]
    public async Task Rejected_secret_shaped_note_is_dropped_and_the_rest_commit()
    {
        var secret = CreateSemantic("leaked", "Use token ghp_" + new string('a', 36) + " for the API.");
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(secret, CreateSemantic("backoff"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.Completed, report.Status);
        Assert.Equal(1, report.AcceptedCount);
        Assert.Single(report.Rejected);
        Assert.Equal(0, report.Rejected[0].Index);
        Assert.Contains(nameof(SoulGuardReason.SecretDetected), report.Rejected[0].Reasons);
        Assert.Single(sink.Commits);
        Assert.Single(sink.Commits[0].Items);
        Assert.Equal($"agents/{Agent}/memory/semantic/backoff.md", sink.Commits[0].Items[0].Path);
    }

    [Fact]
    public async Task Tainted_input_forces_untrusted_tier_on_every_output()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"), CreateSemantic("retries"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000002", MemoryTier.UntrustedDerived));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(2, report.AcceptedCount);
        Assert.All(sink.Commits[0].Items, i =>
        {
            Assert.Contains("trust: T1", i.Content);
            Assert.Contains("tainted: true", i.Content);
        });
    }

    [Fact]
    public async Task External_source_input_also_forces_untrusted_tier()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001", MemoryTier.AgentVerified, ContentTrust.External));
        var sink = new RecordingBatchSink();

        await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Contains("trust: T1", sink.Commits[0].Items[0].Content);
    }

    [Fact]
    public async Task Request_sent_to_the_gateway_offers_no_tools()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply()), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));

        await Runner(new ScriptedGateway(provider), episodes, new RecordingBatchSink(), new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        var request = Assert.Single(provider.Calls);
        Assert.Empty(request.Tools);
    }

    [Fact]
    public async Task Tool_call_in_the_reader_session_is_refused_without_commit()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(
            new ScriptStep.Emit(new Bazaar.Contracts.Gateway.ModelEvent.ToolCallStart("c1", "write_file")),
            new ScriptStep.Emit(new Bazaar.Contracts.Gateway.ModelEvent.ToolCallArgsDelta("c1", "{}")),
            new ScriptStep.Emit(new Bazaar.Contracts.Gateway.ModelEvent.ToolCallEnd("c1")),
            Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.MalformedOutput, report.Status);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Gateway_failure_gives_gateway_failed_and_no_commit()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), new ScriptStep.RateLimited(30));
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.GatewayFailed, report.Status);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Empty_episode_list_makes_no_model_call_and_no_commit()
    {
        var provider = new FakeProvider();
        var episodes = new InMemoryEpisodeSource();
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.Completed, report.Status);
        Assert.Equal(0, report.EpisodeCount);
        Assert.Empty(provider.Calls);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Quarantined_episodes_are_excluded_from_the_run()
    {
        var provider = new FakeProvider();
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001", MemoryTier.Quarantined));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(1, report.QuarantinedExcluded);
        Assert.Empty(provider.Calls);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Failed_commit_reports_commit_failed_and_keeps_episodes()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink { FailNext = true };

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(ConsolidationStatus.CommitFailed, report.Status);
        Assert.Single(await episodes.ReadSinceLastRunAsync(Agent, Ct));
    }

    [Fact]
    public async Task Completed_run_consumes_its_episodes()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(Text(Reply(CreateSemantic("backoff"))), Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));

        await Runner(new ScriptedGateway(provider), episodes, new RecordingBatchSink(), new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Empty(await episodes.ReadSinceLastRunAsync(Agent, Ct));
    }

    [Fact]
    public async Task Deprecation_uses_the_injected_clock_for_its_timestamp()
    {
        var provider = new FakeProvider();
        provider.EnqueueCall(
            Text("{\"operations\":[{\"op\":\"deprecate\",\"target_kind\":\"procedural\",\"slug\":\"old-retry\",\"reason\":\"superseded\",\"replaced_by\":\"backoff\"}]}"),
            Done());
        var episodes = new InMemoryEpisodeSource();
        episodes.Add(Agent, Episode("01J9ZEPISODE00000000000001"));
        var sink = new RecordingBatchSink();

        var report = await Runner(new ScriptedGateway(provider), episodes, sink, new FakeTimeProvider(Start))
            .RunAsync(Agent, new ConsolidationOptions(), Ct);

        Assert.Equal(1, report.AcceptedCount);
        var content = sink.Commits[0].Items[0].Content;
        Assert.Contains("status: deprecated", content);
        Assert.Contains(Start.ToString("o", System.Globalization.CultureInfo.InvariantCulture), content);
        Assert.Contains("[[backoff]]", content);
    }
}
