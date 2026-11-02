using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Packages;

/// <summary>
/// One agent request to install a NuGet package (doc 15, "Approval tiers": new package is Tier 2, capability class install-package).
/// Every field is model-supplied or server-supplied text and may be null or malformed; the policy denies such input.
/// </summary>
/// <param name="PackageId">Package id as the agent wrote it.</param>
/// <param name="VersionRange">Version the agent asks for. Must be an exact pin.</param>
/// <param name="SourceFeed">Feed URL the package comes from. Must be on the operator's allowlist.</param>
/// <param name="RequestingAgentId">Agent making the request. Set by the server, never by the model.</param>
/// <param name="Taint">The requesting session's taint level now.</param>
public sealed record PackageRequest(
    string? PackageId,
    string? VersionRange,
    string? SourceFeed,
    string? RequestingAgentId,
    TaintState Taint);

/// <summary>Why the NuGet policy chose its outcome.</summary>
public enum NugetPolicyReason
{
    /// <summary>The agent id is missing or blank.</summary>
    InvalidRequest,

    /// <summary>The feed is not a valid https URL on the operator's allowlist.</summary>
    FeedNotAllowlisted,

    /// <summary>The package id is blank or does not match the configured pattern.</summary>
    PackageIdNotAllowed,

    /// <summary>The version is not one exact pinned version (floating ranges, wildcards and open ranges are refused).</summary>
    VersionNotExactPin,

    /// <summary>The session is hard-tainted. No package install is allowed from it, even with approval.</summary>
    HardTaintNoPackages,

    /// <summary>A well-formed request. It needs the owner's approval through the tool policy engine (Tier 2).</summary>
    OwnerApprovalRequired,
}

/// <summary>The NuGet policy's answer. It never allows by itself; <see cref="PolicyOutcome.Allow"/> is not a possible result.</summary>
/// <param name="Outcome">Deny, or RequireApproval for a well-formed request.</param>
/// <param name="Reason">Why.</param>
/// <param name="PinnedVersion">The exact version the request pins, without brackets. Null when denied.</param>
/// <param name="CanonicalFeed">The checked feed in canonical form (https, no trailing slash). Null when denied. The approval binds to this value.</param>
public sealed record NugetPackageDecision(PolicyOutcome Outcome, NugetPolicyReason Reason, string? PinnedVersion = null, string? CanonicalFeed = null);
