using Bazaar.Server.Setup.Http;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Progress for <c>GET /setup/status</c> from the registered step list. Every registered step is reported as pending: the apply
/// does not yet publish per-step state, so this never claims a step ran. With no step registered the list is empty. No approval
/// queue feeds the route yet, so none is held.
/// </summary>
public sealed class StepListProgressSource(IReadOnlyList<ISetupStep> steps) : ISetupProgressSource
{
    private static readonly IReadOnlyList<SetupHeldApproval> _noHolds = [];

    /// <inheritdoc />
    public Task<SetupProgressSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetupProgressSnapshot(
            [.. steps.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s => new SetupStepProgress(s.Id, SetupStepState.Pending))],
            _noHolds));
}
