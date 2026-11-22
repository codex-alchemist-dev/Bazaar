namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>A scripted step for <see cref="FakeScorer"/>: a result list, an exception, or a timeout.</summary>
public sealed class FakeScorerStep
{
    private FakeScorerStep(IReadOnlyList<RelevanceResult>? results, Exception? exception)
    {
        Results = results;
        Exception = exception;
    }

    /// <summary>A step that returns these results.</summary>
    public IReadOnlyList<RelevanceResult>? Results { get; }

    /// <summary>A step that throws this exception.</summary>
    public Exception? Exception { get; }

    /// <summary>A step that returns these results.</summary>
    public static FakeScorerStep Returns(IReadOnlyList<RelevanceResult> results) => new(results, null);

    /// <summary>A step that throws.</summary>
    public static FakeScorerStep Throws(Exception exception) => new(null, exception);

    /// <summary>A step that simulates a timeout by throwing <see cref="OperationCanceledException"/>.</summary>
    public static FakeScorerStep Timeout() => new(null, new OperationCanceledException("Scorer timed out."));
}

/// <summary>Scripted <see cref="IRelevanceScorer"/> for tests. Steps are consumed in order; every request is recorded.</summary>
public sealed class FakeScorer : IRelevanceScorer
{
    private readonly Queue<FakeScorerStep> _steps;
    private readonly List<ScoreRequest> _requests = [];

    /// <summary>Creates a scorer that plays back these steps in order.</summary>
    public FakeScorer(IEnumerable<FakeScorerStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = new Queue<FakeScorerStep>(steps);
    }

    /// <summary>Every request received, in call order.</summary>
    public IReadOnlyList<ScoreRequest> Requests => _requests.AsReadOnly();

    /// <inheritdoc />
    public Task<IReadOnlyList<RelevanceResult>> ScoreAsync(ScoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);

        if (ct.IsCancellationRequested)
        {
            return Task.FromException<IReadOnlyList<RelevanceResult>>(new OperationCanceledException(ct));
        }

        if (!_steps.TryDequeue(out var step))
        {
            return Task.FromException<IReadOnlyList<RelevanceResult>>(
                new InvalidOperationException("FakeScorer has no scripted response left."));
        }

        if (step.Exception is not null)
        {
            return Task.FromException<IReadOnlyList<RelevanceResult>>(step.Exception);
        }

        return Task.FromResult(step.Results!);
    }
}
