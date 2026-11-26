namespace Bazaar.GitHub.Webhooks;

/// <summary>
/// Operator settings for webhook ingress (doc 21, Ingress). Bound from <c>Bazaar:GitHub:Webhook</c>. Doc 21 fixes the
/// verification and persistence rules; it does not say where the secret comes from or the size cap, so both are settings
/// with fail-closed defaults.
/// </summary>
public sealed class GitHubWebhookOptions
{
    /// <summary>GitHub's documented maximum webhook payload, 25 MiB.</summary>
    public const int GitHubMaxBodyBytes = 25 * 1024 * 1024;

    /// <summary>
    /// A bootstrap secret reference (<c>env:NAME</c> or <c>file:PATH</c>, resolved by <c>ISecretResolver</c>) for the webhook secret
    /// set on the GitHub App. Empty means ingress is not configured: every delivery is refused with 503, never accepted unsigned.
    /// </summary>
    public string? SecretReference { get; init; }

    /// <summary>Largest body accepted, in bytes. Defaults to GitHub's 25 MiB; an operator may only lower it.</summary>
    public int MaxBodyBytes { get; init; } = GitHubMaxBodyBytes;

    /// <summary>
    /// The opaque path segment for <c>POST /hooks/github/{opaque}</c> (the route doc 21 names). Empty disables that route (404);
    /// <c>POST /github/webhook</c> is always mapped.
    /// </summary>
    public string? OpaqueSegment { get; init; }
}
