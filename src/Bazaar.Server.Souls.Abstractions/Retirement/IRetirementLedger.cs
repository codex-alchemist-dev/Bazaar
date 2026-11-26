using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>
/// Records the latest outcome of each retirement step per subject, so a re-run skips steps that already succeeded.
/// </summary>
public interface IRetirementLedger
{
    /// <summary>The latest recorded result of each step for the subject. Empty when the subject has never been retired.</summary>
    Task<IReadOnlyList<RetirementStepResult>> ReadAsync(SoulRetirementSubject subject, CancellationToken cancellationToken);

    /// <summary>Records a step's outcome, replacing the previous outcome of that step.</summary>
    Task RecordAsync(SoulRetirementSubject subject, RetirementStepResult result, CancellationToken cancellationToken);
}
