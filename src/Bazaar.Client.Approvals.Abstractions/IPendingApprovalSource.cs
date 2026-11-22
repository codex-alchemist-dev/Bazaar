using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals;

/// <summary>
/// Where the phone gets the approvals waiting for the owner (doc 29 Approvals). The head implements it over the
/// authenticated server connection; nothing is cached on the device. Tests use a fake.
/// </summary>
public interface IPendingApprovalSource
{
    /// <summary>The approvals that are pending now, newest first. An empty list means nothing is waiting.</summary>
    ValueTask<IReadOnlyList<ApprovalCardDto>> ListPendingAsync(CancellationToken cancellationToken);
}
