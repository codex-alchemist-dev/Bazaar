using System.Text.RegularExpressions;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Packages;

/// <summary>
/// Operator settings for NuGet installs. The default allowlist holds only nuget.org. Operators may add feeds and
/// change the id pattern; they cannot turn off the pin, hard-taint or Tier 2 rules, which are fixed in code.
/// </summary>
/// <param name="FeedAllowlist">Feed URLs packages may come from. Each must be an absolute https URL.</param>
/// <param name="PackageIdPattern">Regular expression a package id must match in full.</param>
public sealed record NugetPolicyOptions(IReadOnlyList<string> FeedAllowlist, string PackageIdPattern)
{
    /// <summary>The nuget.org v3 service index.</summary>
    public const string NugetOrgFeed = "https://api.nuget.org/v3/index.json";

    /// <summary>NuGet id rules: letters, digits, dot, dash and underscore, starting and ending with a letter or digit, at most 100 characters.</summary>
    public const string DefaultPackageIdPattern = @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?\z";

    /// <summary>The shipped default: nuget.org only, with the NuGet id rules.</summary>
    public static NugetPolicyOptions Default { get; } = new([NugetOrgFeed], DefaultPackageIdPattern);
}

/// <summary>
/// Pure decision over one package request (doc 15 and work package 11). No I/O and no clock; the tool passes the
/// request through <see cref="IToolPolicyEngine"/> afterwards. Fails closed: any input it cannot read is denied.
/// </summary>
public sealed class NugetPackagePolicy
{
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(200);

    // A bare version, or a bracketed one, with no wildcard, comma or parenthesis. NuGet reads a bare version as a minimum,
    // so the install step always pins with brackets; here a bare version is taken as the exact pin the agent meant.
    private static readonly Regex _exactVersion = new(
        @"^[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        _matchTimeout);

    private readonly HashSet<string> _feeds;
    private readonly Regex _packageId;

    /// <summary>Builds the policy. Throws if an allowlisted feed is not an https URL or the id pattern does not compile.</summary>
    /// <exception cref="ArgumentException">An operator setting is malformed. The server fails at startup.</exception>
    public NugetPackagePolicy(NugetPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.FeedAllowlist);
        ArgumentNullException.ThrowIfNull(options.PackageIdPattern);

        _feeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in options.FeedAllowlist)
        {
            var normalized = NormalizeFeed(feed)
                ?? throw new ArgumentException($"Feed '{feed}' is not an absolute https URL.", nameof(options));
            _feeds.Add(normalized);
        }

        try
        {
            _packageId = new Regex(options.PackageIdPattern, RegexOptions.CultureInvariant, _matchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The package id pattern is not a valid regular expression.", nameof(options), ex);
        }
    }

    /// <summary>Judges one request. Order: agent, feed, package id, version, then hard taint.</summary>
    public NugetPackageDecision Evaluate(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RequestingAgentId))
        {
            return Deny(NugetPolicyReason.InvalidRequest);
        }

        var feed = NormalizeFeed(request.SourceFeed);
        if (feed is null || !_feeds.Contains(feed))
        {
            return Deny(NugetPolicyReason.FeedNotAllowlisted);
        }

        if (!IsPackageId(request.PackageId))
        {
            return Deny(NugetPolicyReason.PackageIdNotAllowed);
        }

        var pinned = ExactPin(request.VersionRange);
        if (pinned is null)
        {
            return Deny(NugetPolicyReason.VersionNotExactPin);
        }

        if (request.Taint == TaintState.Hard)
        {
            return Deny(NugetPolicyReason.HardTaintNoPackages);
        }

        return new NugetPackageDecision(PolicyOutcome.RequireApproval, NugetPolicyReason.OwnerApprovalRequired, pinned, feed);
    }

    private static NugetPackageDecision Deny(NugetPolicyReason reason) => new(PolicyOutcome.Deny, reason);

    private bool IsPackageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            return _packageId.IsMatch(id);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The version without brackets when the input is one exact version; null for anything else.</summary>
    private static string? ExactPin(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var text = range.Trim();
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            text = text[1..^1];
        }

        try
        {
            return _exactVersion.IsMatch(text) ? text : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>An absolute https URL without user info, as a comparable string; null when it is not one.</summary>
    private static string? NormalizeFeed(string? feed)
    {
        if (string.IsNullOrWhiteSpace(feed) || !Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }
}
