namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>
/// The App id and PEM private key for one agent's GitHub App. The PEM is secret, so <see cref="ToString"/> redacts it.
/// </summary>
/// <param name="AppId">The numeric GitHub App id.</param>
/// <param name="PrivateKeyPem">The App's RSA private key in PEM form. Never log it.</param>
public sealed record GitHubAppCredentials(long AppId, string PrivateKeyPem)
{
    /// <summary>Returns the id with the key redacted.</summary>
    public override string ToString() => $"GitHubAppCredentials(AppId={AppId}, PrivateKeyPem=[redacted])";
}

/// <summary>
/// Returns the GitHub App credentials for an agent (doc 14: App keys live in <c>ISecretStore</c>). Only host-side code
/// calls this. No VM, agent tool or browser path may reach it.
/// </summary>
public interface IGitHubAppKeyStore
{
    /// <summary>Returns the App credentials for the agent.</summary>
    /// <exception cref="GitHubAppKeyNotFoundException">No App is registered for the agent. The message names no key.</exception>
    ValueTask<GitHubAppCredentials> GetAppAsync(string agentId, CancellationToken cancellationToken);
}
