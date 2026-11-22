namespace Bazaar.Server.Company;

/// <summary>The install's one company profile (doc 45 step 1). Set once by setup; <see cref="OwnerSecondFactorRequired"/> is the owner's recorded choice (doc 45 step 6).</summary>
public sealed record CompanyProfile(
    string Name,
    string Slug,
    string Locale,
    string Timezone,
    bool OwnerSecondFactorRequired,
    DateTimeOffset CreatedAt);

/// <summary>Read access to the company profile for modules that need the company's name, slug, locale or time zone.</summary>
public interface ICompanyProfileReader
{
    /// <summary>The profile, or null before setup created the company.</summary>
    Task<CompanyProfile?> GetAsync(CancellationToken cancellationToken = default);
}
