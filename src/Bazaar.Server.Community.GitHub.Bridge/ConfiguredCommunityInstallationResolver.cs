using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>
/// Resolves installations from the operator's allowlist only. Bazaar.GitHub exposes no App-JWT or installation lookup
/// (its token provider takes an installation id, and App keys live in the Identity Service, doc 14), so there is nothing
/// to reuse for <c>GET /repos/{owner}/{repo}/installation</c>. Unlisted repos return null: fail closed, no writes.
/// An exact owner/repo entry wins over an owner wildcard. Nothing is cached beyond the validated option values.
/// </summary>
public sealed class ConfiguredCommunityInstallationResolver(IOptions<CommunityInstallationOptions> options) : ICommunityInstallationResolver
{
    public Task<CommunityInstallation?> ResolveAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return Task.FromResult<CommunityInstallation?>(null);
        }

        CommunityInstallationEntry? wildcard = null;
        foreach (var e in options.Value.Entries)
        {
            if (!string.Equals(e.Owner, owner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(e.Repo, repo, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<CommunityInstallation?>(new CommunityInstallation(e.InstallationId, e.RepositoryId));
            }

            if (e.Repo == "*")
            {
                wildcard = e;
            }
        }

        return Task.FromResult<CommunityInstallation?>(wildcard is null ? null : new CommunityInstallation(wildcard.InstallationId, wildcard.RepositoryId));
    }
}
