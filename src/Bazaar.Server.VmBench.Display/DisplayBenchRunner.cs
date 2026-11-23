using Microsoft.Extensions.Options;
using static Bazaar.Server.VmBench.Display.DisplayBenchSupport;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// Runs the display scenarios. The criteria are validated and copied into an immutable snapshot before the first scenario starts;
/// scenarios, verdicts and the report all use that snapshot, so nothing that happens during the run can change what counts as a pass.
/// A scenario that throws is reported as failed, never as a pass; caller cancellation propagates.
/// </summary>
public sealed class DisplayBenchRunner : IDisplayBenchRunner
{
    private readonly IReadOnlyList<IDisplayBenchScenario> _scenarios;
    private readonly IOptions<DisplayBenchCriteria> _criteria;
    private readonly TimeProvider _time;

    public DisplayBenchRunner(IEnumerable<IDisplayBenchScenario> scenarios, IOptions<DisplayBenchCriteria> criteria, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(criteria);
        _scenarios = scenarios.OrderBy(s => s.Id).ToArray();
        _criteria = criteria;
        _time = time ?? TimeProvider.System;
    }

    public async Task<DisplayBenchReport> RunAsync(string hostLabel, IReadOnlyList<DisplayBenchScenario>? scenarios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hostLabel);

        var live = _criteria.Value;
        var errors = DisplayBenchCriteriaValidator.Check(live);
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(nameof(DisplayBenchCriteria), typeof(DisplayBenchCriteria), errors);
        }

        var snapshot = DisplayBenchCriteriaSnapshot.From(live);
        var context = new DisplayBenchContext(snapshot, _time);
        var wanted = scenarios is { Count: > 0 } ? scenarios.ToHashSet() : null;
        var startedAt = _time.GetUtcNow();
        var results = new List<DisplayBenchResult>();

        foreach (var scenario in _scenarios.Where(s => wanted is null || wanted.Contains(s.Id)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(await scenario.RunAsync(context, ct));
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, ct))
            {
                results.Add(Failed(scenario.Id, "scenario", "n/a", Describe(ex)));
            }
        }

        return new DisplayBenchReport(
            RunId: Guid.NewGuid().ToString("N"),
            StartedAt: startedAt,
            FinishedAt: _time.GetUtcNow(),
            HostLabel: hostLabel,
            Criteria: snapshot,
            Results: results,
            Overall: Overall(results));
    }

    /// <summary>Passed only when something ran and everything passed; any failure fails; otherwise not run.</summary>
    public static DisplayBenchOutcome Overall(IReadOnlyList<DisplayBenchResult> results)
    {
        if (results.Any(r => r.Outcome == DisplayBenchOutcome.Failed))
        {
            return DisplayBenchOutcome.Failed;
        }

        return results.Count > 0 && results.All(r => r.Outcome == DisplayBenchOutcome.Passed)
            ? DisplayBenchOutcome.Passed
            : DisplayBenchOutcome.NotRun;
    }
}
