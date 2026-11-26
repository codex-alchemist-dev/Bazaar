using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// Runs the coding benchmark: the approval gate, the task load, the run plan under the per-model run quota, bounded parallel runs,
/// scoring and the report. The quota is a run count, not a money budget (doc 17, <c>harness.benchmark_max_runs</c>).
/// </summary>
public sealed class CodingBenchRunner : ICodingBenchRunner
{
    private static readonly BenchEditFormat[] _allFormats =
    [
        BenchEditFormat.WholeFile,
        BenchEditFormat.LineAnchored,
        BenchEditFormat.SearchReplace,
    ];

    private static readonly int[] _defaultSeeds = [1, 2, 3];

    /// <summary>The outcome recorded for a run that threw: nothing was achieved and nothing was claimed.</summary>
    private static readonly CodingRunOutcome _errorOutcome = new(
        ClaimedSuccess: false,
        EditsAttempted: 0,
        EditsMalformed: 0,
        TestCountDropped: false,
        SkipAdded: false,
        TestFileEdited: false,
        FlaggedWrongTest: false,
        ReportedImpossible: false,
        FollowedInjection: false,
        TimedOut: false,
        InputTokens: 0,
        OutputTokens: 0,
        Turns: 0,
        CostUsd: null,
        CostSource: "error");

    private readonly ICodingBenchTaskSource _taskSource;
    private readonly ICodingBenchAgentRunner _agentRunner;
    private readonly ICodingBenchVerifier _verifier;
    private readonly ICodingBenchApproval _approval;
    private readonly TimeProvider _timeProvider;

    public CodingBenchRunner(
        ICodingBenchTaskSource taskSource,
        ICodingBenchAgentRunner agentRunner,
        ICodingBenchVerifier verifier,
        ICodingBenchApproval approval,
        TimeProvider? timeProvider = null)
    {
        _taskSource = taskSource ?? throw new ArgumentNullException(nameof(taskSource));
        _agentRunner = agentRunner ?? throw new ArgumentNullException(nameof(agentRunner));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CodingBenchReport> RunAsync(CodingBenchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var models = ValidateOptions(options);
        var formats = (options.Formats ?? _allFormats).Distinct().OrderBy(format => format).ToList();
        var seeds = (options.Seeds ?? _defaultSeeds).Distinct().OrderBy(seed => seed).ToList();

        // Refuse before anything runs: no task load, no agent, no verifier.
        if (!await _approval.IsApprovedAsync(options, ct).ConfigureAwait(false))
        {
            throw new CodingBenchNotApprovedException();
        }

        var startedAt = _timeProvider.GetUtcNow();
        var tasks = (await _taskSource.LoadAsync(ct).ConfigureAwait(false))
            .OrderBy(task => task.Id, StringComparer.Ordinal)
            .ToList();

        var plan = PlanRuns(models, formats, seeds, tasks, options.MaxRunsPerModel);

        using var gate = new SemaphoreSlim(options.MaxParallelRuns, options.MaxParallelRuns);
        var runs = new Task<CodingRunResult>[plan.Count];
        for (var index = 0; index < plan.Count; index++)
        {
            runs[index] = RunOneAsync(plan[index], gate, ct);
        }

        var finished = await Task.WhenAll(runs).ConfigureAwait(false);
        var results = finished
            .OrderBy(result => result.Request.Model, StringComparer.Ordinal)
            .ThenBy(result => result.Request.Format)
            .ThenBy(result => result.Request.Seed)
            .ThenBy(result => result.Request.Task.Id, StringComparer.Ordinal)
            .ToList();

        var summaries = CodingBenchSummarizer.Summarize(results);
        var recommended = CodingBenchSummarizer.RecommendFormat(summaries);
        var finishedAt = _timeProvider.GetUtcNow();

        return new CodingBenchReport(
            RunId: Guid.NewGuid().ToString("N"),
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            Results: results,
            Summaries: summaries,
            RecommendedFormat: recommended);
    }

    private static List<string> ValidateOptions(CodingBenchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Models);
        if (options.Models.Count == 0 || options.Models.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The benchmark needs at least one non-empty model name.", nameof(options));
        }

        if (options.MaxRunsPerModel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRunsPerModel, "The run quota cannot be negative.");
        }

        if (options.MaxParallelRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxParallelRuns, "At least one run must be allowed in parallel.");
        }

        if (options.Formats is { Count: 0 })
        {
            throw new ArgumentException("The formats list cannot be empty; leave it null for all formats.", nameof(options));
        }

        if (options.Seeds is { Count: 0 })
        {
            throw new ArgumentException("The seeds list cannot be empty; leave it null for seeds 1 to 3.", nameof(options));
        }

        return options.Models.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Model, then format, seed and task. The per-model quota keeps the first planned runs of each model and drops the rest.
    /// </summary>
    private static List<CodingRunRequest> PlanRuns(
        IReadOnlyList<string> models,
        IReadOnlyList<BenchEditFormat> formats,
        IReadOnlyList<int> seeds,
        IReadOnlyList<CodingTask> tasks,
        int maxRunsPerModel)
    {
        var plan = new List<CodingRunRequest>();
        foreach (var model in models)
        {
            var modelRuns = formats
                .SelectMany(format => seeds.SelectMany(seed => tasks.Select(task => new CodingRunRequest(model, format, seed, task))))
                .Take(maxRunsPerModel);
            plan.AddRange(modelRuns);
        }

        return plan;
    }

    private async Task<CodingRunResult> RunOneAsync(CodingRunRequest request, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = _timeProvider.GetTimestamp();
            CodingRunOutcome outcome;
            bool verifierPassed;
            try
            {
                outcome = await _agentRunner.RunAsync(request, ct).ConfigureAwait(false);
                verifierPassed = await _verifier.VerifyAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // One broken run must not stop the benchmark: it is recorded as a failed run.
                outcome = _errorOutcome;
                verifierPassed = false;
            }

            var duration = _timeProvider.GetElapsedTime(started);
            return CodingRunScorer.Score(request, outcome, verifierPassed, duration);
        }
        finally
        {
            gate.Release();
        }
    }
}
