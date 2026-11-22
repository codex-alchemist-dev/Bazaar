using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles;

/// <summary>Default gate: refuses every widening until the approval and biometric modules register their own (fail closed).</summary>
public sealed class DenyAllWideningGate : IGrantWideningGate
{
    public Task<bool> ApproveAsync(GrantWideningRequest request, CancellationToken ct = default) => Task.FromResult(false);
}
