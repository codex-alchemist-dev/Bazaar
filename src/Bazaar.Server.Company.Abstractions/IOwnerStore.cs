namespace Bazaar.Server.Company;

/// <summary>
/// What the owner step writes. <see cref="DisplayName"/> is personal data and never goes to an audit row. <see cref="RecoveryBundleRef"/>
/// is a <c>secret://bazaar/</c> reference only: the bundle itself, any code and any key are never part of the plan or of this record.
/// </summary>
public sealed record OwnerRequest(string DisplayName, bool RequireSecondFactor, string? RecoveryBundleRef, bool PrintedCodes);

/// <summary>What one owner seed created. The owner row and the choices row are tracked apart, so a rollback removes only what this run wrote.</summary>
public sealed record OwnerOutcome(Guid? OwnerCreatedId, bool ChoicesCreated)
{
    /// <summary>True when the call created nothing.</summary>
    public bool IsEmpty => OwnerCreatedId is null && !ChoicesCreated;
}

/// <summary>The result of <see cref="IOwnerStore.EnsureAsync"/>: an outcome, or a refusal with a message that names no value.</summary>
public sealed record OwnerResult(OwnerOutcome? Outcome, string? Refusal)
{
    /// <summary>True when the seed ran to the end.</summary>
    public bool Succeeded => Outcome is not null;

    /// <summary>A refusal: nothing was created.</summary>
    public static OwnerResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// The store seam of the <c>owner.create</c> step: the owner <c>human</c> row (role owner, status active) and the owner's recorded
/// choices (second-factor requirement, recovery bundle reference, printed-codes flag). No passkey and no recovery material is created
/// here: the owner enrols a second factor through the sign-in page. Idempotent; an owner with another display name, or recorded choices
/// that differ, are refused, never overwritten.
/// </summary>
public interface IOwnerStore
{
    /// <summary>Creates what is missing in one transaction, or refuses a conflict.</summary>
    Task<OwnerResult> EnsureAsync(OwnerRequest request, CancellationToken cancellationToken);

    /// <summary>Rollback: removes what the outcome says one earlier call created. A row something now points at (a device, a credential) stays.</summary>
    Task RollbackAsync(OwnerOutcome outcome, CancellationToken cancellationToken);
}
