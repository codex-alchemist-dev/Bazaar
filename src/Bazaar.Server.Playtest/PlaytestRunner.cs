using Bazaar.Server.Playtest.Abstractions;

namespace Bazaar.Server.Playtest;

/// <summary>
/// Tier A orchestration (doc 26 section 7): scenario, headless host, state dump hash, assertions, project-hash check, note writer.
/// The run fails when the project hash moved, because a playtest never edits project files. It never creates Backlog cards: findings
/// become cards only when the owner marks them.
/// </summary>
public sealed class PlaytestRunner(
    IEngineHeadlessHost host,
    IProjectHasher hasher,
    IPlaytestNoteWriter noteWriter,
    IPlaytestRunStore store,
    PlaytestSettings settings,
    TimeProvider? timeProvider = null) : IPlaytestRunner
{
    private readonly PlaytestSettings _settings = settings.Validated();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PlaytestResult> RunAsync(PlaytestScenario scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Timeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), "Scenario timeout must be at least one second.");
        }

        var runId = Guid.CreateVersion7();

        // The profile is a contract: refuse before anything runs when the host cannot promise no secrets and no egress.
        if (!host.RequiredProfile.Satisfies(HostProfile.Required))
        {
            return await FinishAsync(Build(runId, scenario, PlaytestStatus.ProfileRejected, null, [], null, null), null, cancellationToken);
        }

        var before = await hasher.HashAsync(scenario.BuildRef, cancellationToken);

        StateDump dump;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(scenario.Timeout);
            try
            {
                dump = await host.RunAsync(scenario, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await FinishAsync(Build(runId, scenario, PlaytestStatus.TimedOut, null, [], before, null), null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await FinishAsync(Build(runId, scenario, PlaytestStatus.HostFailed, null, [], before, null), null, cancellationToken);
            }
        }

        var after = await hasher.HashAsync(scenario.BuildRef, cancellationToken);
        var results = Evaluate(scenario, dump);

        var status = !string.Equals(before, after, StringComparison.Ordinal)
            ? PlaytestStatus.ProjectChanged
            : results.All(r => r.Passed) ? PlaytestStatus.Passed : PlaytestStatus.AssertionsFailed;

        return await FinishAsync(Build(runId, scenario, status, dump.ContentHash(), results, before, after), dump, cancellationToken);
    }

    private static List<AssertionResult> Evaluate(PlaytestScenario scenario, StateDump dump)
    {
        var results = new List<AssertionResult>(scenario.Assertions.Count);
        foreach (var assertion in scenario.Assertions)
        {
            var found = dump.Values.TryGetValue(assertion.Key, out var actual);
            results.Add(new AssertionResult(assertion.Id, found && string.Equals(actual, assertion.Expected, StringComparison.Ordinal), found ? actual : null));
        }

        return results;
    }

    private PlaytestResult Build(
        Guid runId, PlaytestScenario scenario, PlaytestStatus status, string? dumpHash,
        IReadOnlyList<AssertionResult> results, string? before, string? after) =>
        new(runId, scenario, PlaytestMode.TierA, status, dumpHash, results, before, after, _time.GetUtcNow());

    private async Task<PlaytestResult> FinishAsync(PlaytestResult result, StateDump? dump, CancellationToken cancellationToken)
    {
        await store.SaveAsync(result, cancellationToken);

        // The reader session gets data only. A rejected or failed run has no dump, so nothing is handed over for it.
        if (dump is not null)
        {
            await noteWriter.WriteNotesAsync(
                new PlaytestNoteRequest(result.RunId, result.Scenario.ScenarioId, result.Status, dump.Values, result.Assertions, _settings.HostedVision),
                cancellationToken);
        }

        return result;
    }
}
