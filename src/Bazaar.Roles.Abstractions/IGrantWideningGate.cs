namespace Bazaar.Roles.Abstractions;

/// <summary>What a widening change would do, shown to the gate.</summary>
/// <param name="Actor">Who asks for the change.</param>
/// <param name="Description">One line for the approval prompt.</param>
/// <param name="RoleId">The role changed or assigned.</param>
/// <param name="PrincipalId">The principal who gains grants, or null when a role edit widens humans who already hold it.</param>
/// <param name="Added">The grants that are new to the human principals concerned.</param>
public sealed record GrantWideningRequest(RoleActor Actor, string Description, Guid RoleId, Guid? PrincipalId, IReadOnlyList<Grant> Added);

/// <summary>
/// The extra step a change that widens a human principal's grants needs (doc 48 section 4): the owner's signed approval
/// (doc 32 section 5a) and a biometric check (section 8). Those pieces are separate modules, so they plug in here. The default
/// registration refuses every request, so a widening is impossible until the approval and biometric modules are wired.
/// </summary>
public interface IGrantWideningGate
{
    /// <summary>True only when the owner's signed approval and the biometric check both passed for this exact change.</summary>
    Task<bool> ApproveAsync(GrantWideningRequest request, CancellationToken ct = default);
}
