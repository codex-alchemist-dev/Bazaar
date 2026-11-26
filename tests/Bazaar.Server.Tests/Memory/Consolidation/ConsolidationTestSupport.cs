using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Memory.Consolidation;
using Bazaar.Server.Memory.Consolidation.Fakes;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Memory.Notes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Memory.Consolidation;

/// <summary>Adapts the scripted <see cref="FakeProvider"/> to <see cref="IModelGateway"/>, optionally moving a fake clock between events.</summary>
internal sealed class ScriptedGateway(FakeProvider provider, FakeTimeProvider? time = null, TimeSpan advancePerEvent = default) : IModelGateway
{
    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken) =>
        Play(request, cancellationToken);

    private async IAsyncEnumerable<ModelEvent> Play(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var ev in provider.StreamAsync(request, cancellationToken))
        {
            if (time is not null && advancePerEvent > TimeSpan.Zero)
            {
                time.Advance(advancePerEvent);
            }

            yield return ev;
        }
    }
}

/// <summary>Batch sink that records every commit and can be told to fail.</summary>
internal sealed class RecordingBatchSink : IMemoryBatchSink
{
    public List<MemoryBatch> Commits { get; } = [];

    public bool FailNext { get; set; }

    public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Commits.Add(batch);
        if (FailNext)
        {
            FailNext = false;
            return Task.FromResult(MemoryCommitResult.Failure("disk full"));
        }

        return Task.FromResult(MemoryCommitResult.Success("abc123"));
    }
}

internal static class ConsolidationTestSupport
{
    public const string Agent = "alpha";
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    public static EpisodeNote Episode(string id, MemoryTier trust = MemoryTier.AgentVerified, ContentTrust source = ContentTrust.Agent) =>
        new(
            id,
            $"agents/{Agent}/memory/episodes/2026-11-01/{id}.md",
            NoteFixtures.Text(
                NoteFixtures.Note(MemoryKind.Episodic, source, trust) with { Id = id, Type = MemoryNoteTypes.TypeFor(MemoryKind.Episodic) },
                "Retry with backoff worked after the second failure."),
            trust,
            source);

    public static string Reply(params string[] operations) =>
        "{\"operations\":[" + string.Join(",", operations) + "],\"maintenance_report\":\"Consolidated two episodes.\"}";

    public static string CreateSemantic(string slug, string body = "Retry with exponential backoff.") =>
        "{\"op\":\"create_semantic\",\"slug\":\"" + slug + "\",\"title\":\"Backoff\",\"body\":\"" + body + "\"}";

    public static ScriptStep Text(string text) => new ScriptStep.Emit(new ModelEvent.TextDelta(text));

    public static ScriptStep Usage(long input, long output) =>
        new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(input, 0, 0, 0, output, 0)));

    public static ScriptStep Done() => new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "fake-model"));

    public static ConsolidationRunner Runner(IModelGateway gateway, InMemoryEpisodeSource episodes, RecordingBatchSink sink, TimeProvider time, InMemoryConsolidationLock? locks = null) =>
        new(gateway, episodes, locks ?? new InMemoryConsolidationLock(), sink, time);
}
