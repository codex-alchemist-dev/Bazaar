namespace Bazaar.Server.Community.GitHub.Bridge;

/// <summary>The GitHub App installation and repository id that cover one repository.</summary>
public sealed record CommunityInstallation(long InstallationId, long RepositoryId);

/// <summary>
/// Finds the installation for an owner/repo. Bazaar.Server.GitHub has no such lookup (its adapter and token provider take an installation id),
/// so the host supplies this seam, for example from the Identity Service's installation registry. Returns null when the App is not installed there.
/// </summary>
public interface ICommunityInstallationResolver
{
    Task<CommunityInstallation?> ResolveAsync(string owner, string repo, CancellationToken cancellationToken);
}
