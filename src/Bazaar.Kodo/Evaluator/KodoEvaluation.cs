namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// What the evaluator remembers between samples. The caller keeps it and passes it back on the next
/// evaluation. Kodo starts from <see cref="KodoEvaluator.Initial"/>.
/// </summary>
/// <param name="Underlying">The underlying health state. Never <see cref="KodoState.Frozen"/>.</param>
/// <param name="GreenSince">When the current run of all-green samples began, or null when the last sample was not green.</param>
/// <param name="Transitions">Times of recent changes to <paramref name="Underlying"/>, pruned to the flap window.</param>
/// <param name="Flapping">Whether the last evaluation was flapping.</param>
public sealed record KodoEvaluatorMemory(
    KodoState Underlying,
    DateTimeOffset? GreenSince,
    IReadOnlyList<DateTimeOffset> Transitions,
    bool Flapping);

/// <summary>
/// The result of one evaluation.
/// </summary>
/// <param name="State">What to show: <see cref="KodoState.Frozen"/> while incident mode is active, otherwise the underlying state.</param>
/// <param name="Underlying">The underlying health state, shown beside <see cref="KodoState.Frozen"/>.</param>
/// <param name="Flapping">Flap detection is holding the state (section 8.2).</param>
/// <param name="RaiseFlapAlert">True only on the evaluation that starts flapping, so one grouped alert is raised (section 8.2).</param>
/// <param name="Next">The memory to pass to the next evaluation.</param>
public sealed record KodoEvaluation(
    KodoState State,
    KodoState Underlying,
    bool Flapping,
    bool RaiseFlapAlert,
    KodoEvaluatorMemory Next);
