using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// The default approval: nothing runs until an approval is recorded. The real approval is the owner approval card
/// through the approvals module; this default only keeps the benchmark closed when that module is not wired.
/// </summary>
public sealed class DenyUntilApprovedCodingBenchApproval : ICodingBenchApproval
{
    public Task<bool> IsApprovedAsync(CodingBenchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

/// <summary>
/// A fixed approval answer, for tests and for hosts that have already recorded the owner's decision.
/// Production approval goes through the owner approval card in the approvals module, not this class.
/// </summary>
public sealed class FixedCodingBenchApproval : ICodingBenchApproval
{
    private readonly bool _approved;

    public FixedCodingBenchApproval(bool approved)
    {
        _approved = approved;
    }

    public Task<bool> IsApprovedAsync(CodingBenchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_approved);
    }
}
