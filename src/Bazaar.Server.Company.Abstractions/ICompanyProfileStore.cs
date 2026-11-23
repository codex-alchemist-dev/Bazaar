namespace Bazaar.Server.Company;

/// <summary>What the profile step writes. Values come from the validated plan.</summary>
public sealed record CompanyProfileRequest(string Name, string Slug, string Locale, string Timezone);

/// <summary>The result of <see cref="ICompanyProfileStore.EnsureAsync"/>: created, already there, or refused with a message that names no value.</summary>
public sealed record CompanyProfileResult(bool Created, string? Refusal)
{
    /// <summary>True when the profile now matches the request.</summary>
    public bool Succeeded => Refusal is null;

    /// <summary>The profile was written by this call.</summary>
    public static CompanyProfileResult WasCreated { get; } = new(true, null);

    /// <summary>The profile already held exactly these values.</summary>
    public static CompanyProfileResult AlreadyPresent { get; } = new(false, null);

    /// <summary>A conflicting profile exists; nothing was written.</summary>
    public static CompanyProfileResult Refused(string reason) => new(false, reason);
}

/// <summary>
/// The store seam of the <c>company.profile</c> step. Idempotent: a profile with the same values is left alone, a profile with other
/// values is refused (one company per install; setup never edits it). The Postgres implementation is in <c>Bazaar.Server.Company</c>.
/// </summary>
public interface ICompanyProfileStore
{
    /// <summary>Creates the one profile row, or finds the same one, or refuses a conflicting one.</summary>
    Task<CompanyProfileResult> EnsureAsync(CompanyProfileRequest request, CancellationToken cancellationToken);

    /// <summary>Rollback: removes the profile row with this slug. Called only for a row the same apply created.</summary>
    Task RemoveAsync(string slug, CancellationToken cancellationToken);
}
