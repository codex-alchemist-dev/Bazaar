using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>
/// Process-local <see cref="IRetirementLedger"/>. Lost on restart, so it is for tests and single-run use only. Production
/// hosts should use <see cref="PostgresRetirementLedger"/>.
/// </summary>
public sealed class InMemoryRetirementLedger : IRetirementLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<SoulRetirementSubject, Dictionary<RetirementStepKind, RetirementStepResult>> _bySubject = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<RetirementStepResult>> ReadAsync(SoulRetirementSubject subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<RetirementStepResult> results = _bySubject.TryGetValue(subject, out var steps)
                ? steps.Values.ToList()
                : [];
            return Task.FromResult(results);
        }
    }

    /// <inheritdoc />
    public Task RecordAsync(SoulRetirementSubject subject, RetirementStepResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bySubject.TryGetValue(subject, out var steps))
                _bySubject[subject] = steps = [];
            steps[result.Step] = result;
        }
        return Task.CompletedTask;
    }
}
