using Bazaar.Community.Drafts;

namespace Bazaar.Community.Policy;

/// <summary>
/// <see cref="ICommunityPolicySource"/> over <see cref="PlatformPolicyLoader"/> (doc 36 WP8). Reads the table on every call, so an
/// operator's edit to <c>platform_policy</c> applies at once, and falls back to the shipped seed only while the table is empty.
/// The platform key matches ignoring case. A database error propagates: the caller must not guess a policy.
/// </summary>
public sealed class PlatformPolicySource(PlatformPolicyLoader loader) : ICommunityPolicySource
{
    /// <inheritdoc />
    public async Task<PlatformPolicy?> FindAsync(string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var all = await loader.LoadOrSeedAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(p => string.Equals(p.Platform, platform, StringComparison.OrdinalIgnoreCase));
    }
}
