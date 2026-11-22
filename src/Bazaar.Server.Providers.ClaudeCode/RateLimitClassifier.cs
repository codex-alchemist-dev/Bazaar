using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>What a failed call turned out to be (docs/design/91 section 9).</summary>
internal enum FailureClass
{
    None,
    RateLimited,
    Account,
    Transient,
    Other,
}

internal sealed record ClassifiedFailure(FailureClass Class, string Detail, TimeSpan? RetryAfter);

/// <summary>Reads usage-limit and account errors out of CLI text. The wording is not a stable contract, so matching is tolerant.</summary>
internal static partial class RateLimitClassifier
{
    private static readonly string[] _accountErrors = ["authentication_failed", "oauth_org_not_allowed", "account_on_hold", "billing_error"];

    [GeneratedRegex(@"(usage|rate)\s+limit|limit\s+reached|too many requests|\b429\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimitText();

    [GeneratedRegex(@"limit reached\|(\d{10})", RegexOptions.CultureInvariant)]
    private static partial Regex EpochForm();

    [GeneratedRegex(@"(?:in|after)\s+(\d+)\s*(hours?|hrs?|minutes?|mins?|seconds?|secs?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeForm();

    public static ClassifiedFailure Classify(string? apiRetryError, string? text, DateTimeOffset now, ClaudeCodeOptions options)
    {
        if (apiRetryError is not null && _accountErrors.Contains(apiRetryError, StringComparer.Ordinal))
        {
            return new ClassifiedFailure(FailureClass.Account, apiRetryError, null);
        }

        text ??= "";
        foreach (var e in _accountErrors)
        {
            if (text.Contains(e, StringComparison.Ordinal))
            {
                return new ClassifiedFailure(FailureClass.Account, e, null);
            }
        }

        if (string.Equals(apiRetryError, "rate_limit", StringComparison.Ordinal) || LimitText().IsMatch(text))
        {
            return new ClassifiedFailure(FailureClass.RateLimited, "rate_limit", Clamp(Parse(text, now) ?? options.RateLimitDefaultBackoff, options));
        }

        if (apiRetryError is "overloaded" or "server_error")
        {
            return new ClassifiedFailure(FailureClass.Transient, apiRetryError, null);
        }

        return new ClassifiedFailure(FailureClass.Other, apiRetryError ?? "unknown", null);
    }

    private static TimeSpan? Parse(string text, DateTimeOffset now)
    {
        var epoch = EpochForm().Match(text);
        if (epoch.Success && long.TryParse(epoch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var secs))
        {
            var d = DateTimeOffset.FromUnixTimeSeconds(secs) - now;
            return d > TimeSpan.Zero ? d : null;
        }

        var rel = RelativeForm().Match(text);
        if (rel.Success && int.TryParse(rel.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            var unit = rel.Groups[2].Value.ToLowerInvariant();
            return unit.StartsWith('h') ? TimeSpan.FromHours(n) : unit.StartsWith("mi", StringComparison.Ordinal) ? TimeSpan.FromMinutes(n) : TimeSpan.FromSeconds(n);
        }

        return null;
    }

    private static TimeSpan Clamp(TimeSpan value, ClaudeCodeOptions options) =>
        value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value > options.RateLimitMaxBackoff ? options.RateLimitMaxBackoff : value;
}
