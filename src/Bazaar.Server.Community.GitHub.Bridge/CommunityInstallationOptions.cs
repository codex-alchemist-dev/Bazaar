using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>
/// The operator's allowlist of community repositories (doc 21 <c>github.community_repo_allowlist</c>), bound from
/// <c>Bazaar:Community:GitHub:Installations</c>. Repos not covered here are never written to.
/// </summary>
public sealed class CommunityInstallationOptions
{
    public const string SectionName = "Bazaar:Community:GitHub:Installations";

    public List<CommunityInstallationEntry> Entries { get; set; } = [];
}

/// <summary>One allowlist entry. <see cref="Repo"/> is a repository name or <c>*</c> (every repo of that owner, an explicit opt-in).</summary>
public sealed class CommunityInstallationEntry
{
    public string Owner { get; set; } = "";

    public string Repo { get; set; } = "";

    public long InstallationId { get; set; }

    /// <summary>The repository id returned for a match. For a <c>*</c> entry it is returned for every repo of the owner as configured.</summary>
    public long RepositoryId { get; set; }
}

public sealed class CommunityInstallationOptionsValidator : IValidateOptions<CommunityInstallationOptions>
{
    public ValidateOptionsResult Validate(string? name, CommunityInstallationOptions options)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Entries.Count; i++)
        {
            var e = options.Entries[i];
            var at = $"{CommunityInstallationOptions.SectionName}:Entries:{i}";
            if (string.IsNullOrWhiteSpace(e.Owner) || e.Owner == "*" || e.Owner.Contains('/', StringComparison.Ordinal))
            {
                errors.Add($"{at}: owner must be a concrete owner name.");
            }

            if (string.IsNullOrWhiteSpace(e.Repo) || e.Repo.Contains('/', StringComparison.Ordinal))
            {
                errors.Add($"{at}: repo must be a repository name or '*'.");
            }

            if (e.InstallationId <= 0)
            {
                errors.Add($"{at}: installationId must be positive.");
            }

            if (e.RepositoryId <= 0)
            {
                errors.Add($"{at}: repositoryId must be positive.");
            }

            if (!seen.Add($"{e.Owner}/{e.Repo}"))
            {
                errors.Add($"{at}: duplicate entry for {e.Owner}/{e.Repo}.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
