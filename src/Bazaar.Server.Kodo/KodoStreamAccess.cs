using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Decides who may follow the <c>kodo</c> stream on <c>/ws</c> (doc 46 sections 14.5 and 15.2). The host passes the same role
/// decision it gives <see cref="KodoEndpoints.MapBazaarKodo"/> as its guard (the <c>kodo.view.status</c> grant), so this module
/// never evaluates roles itself. Every registration is TryAdd, so a host substitutes first.
/// </summary>
public interface IKodoStreamAccess
{
    /// <summary>True when the principal may subscribe to the <c>kodo</c> stream.</summary>
    ValueTask<bool> CanViewAsync(Guid principalId, PrincipalKind principalKind, CancellationToken cancellationToken);
}

/// <summary>
/// The default access: the owner only. Custom role grants (doc 48) are not built, and the owner holds every Kodo permission, so the
/// most protective answer is the safe default (agents and other humans are refused until a host supplies a role-based check).
/// </summary>
public sealed class OwnerOnlyKodoStreamAccess : IKodoStreamAccess
{
    public ValueTask<bool> CanViewAsync(Guid principalId, PrincipalKind principalKind, CancellationToken cancellationToken) =>
        ValueTask.FromResult(principalKind == PrincipalKind.Owner);
}
