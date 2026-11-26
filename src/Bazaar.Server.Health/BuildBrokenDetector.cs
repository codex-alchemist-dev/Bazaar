using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>
/// Finds the pass-to-fail transitions in the CI runs the collection job reads (doc 24 section 3 build-broken seam). Keeps, per
/// project, the outcome of the newest run seen and a watermark, so the same runs read again on the next pass raise nothing and
/// a long red streak raises once. The first sight of a project only sets the baseline. State is in memory.
/// </summary>
public sealed class BuildBrokenDetector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);

    private sealed class State
    {
        public required bool LastPassed { get; set; }
        public required DateTimeOffset At { get; set; }
        public required string RunId { get; set; }
    }

    /// <summary>Feeds the runs one pass read for <paramref name="projectId"/> and returns the breaks they contain, oldest first.</summary>
    public IReadOnlyList<BuildBroken> Observe(string projectId, IEnumerable<CiRunFact> runs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(runs);

        var ordered = runs.OrderBy(r => r.CompletedAt).ThenBy(r => r.RunId, StringComparer.Ordinal).ToList();
        var broken = new List<BuildBroken>();
        lock (_gate)
        {
            if (!_state.TryGetValue(projectId, out var state))
            {
                if (ordered.Count == 0)
                    return broken;

                // First sight: the newest run is the baseline; nothing in this batch counts as a break.
                var last = ordered[^1];
                _state[projectId] = new State { LastPassed = last.Passed, At = last.CompletedAt, RunId = last.RunId };
                return broken;
            }

            foreach (var run in ordered)
            {
                if (Compare(run, state) <= 0)
                    continue; // Already seen on an earlier pass.

                if (state.LastPassed && !run.Passed)
                    broken.Add(new BuildBroken(projectId, run.RunId, run.CompletedAt));
                state.LastPassed = run.Passed;
                state.At = run.CompletedAt;
                state.RunId = run.RunId;
            }
        }

        return broken;
    }

    private static int Compare(CiRunFact run, State state)
    {
        var c = run.CompletedAt.CompareTo(state.At);
        return c != 0 ? c : string.CompareOrdinal(run.RunId, state.RunId);
    }
}
