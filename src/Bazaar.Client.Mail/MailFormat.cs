using System.Globalization;
using Bazaar.Client.Mail.Abstractions;

namespace Bazaar.Client.Mail;

/// <summary>
/// Display text for the mail panes. The view models carry data and the views show these strings. Every string this returns
/// is plain text, and no input is ever parsed as markup.
/// </summary>
public static class MailFormat
{
    public static string QuarantineState(MailQuarantineState state) => state switch
    {
        MailQuarantineState.Held => "held",
        MailQuarantineState.Released => "released",
        MailQuarantineState.Purged => "purged",
        MailQuarantineState.Reported => "reported",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>Reason codes joined in order, or a plain note when the list is empty.</summary>
    public static string Reasons(IReadOnlyList<string> reasons) =>
        reasons.Count == 0 ? "no reason codes" : string.Join(", ", reasons);

    /// <summary>A received time in UTC, to the minute.</summary>
    public static string Received(DateTimeOffset receivedAt) =>
        receivedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>
    /// The host a link points at, which is the only part of a link the client shows. The URL is parsed, never fetched or
    /// opened. A link with no absolute host (for example <c>javascript:</c>) reads as "no host".
    /// </summary>
    public static string LinkHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : "no host";

    public static string DnsKind(MailDnsCheckKind kind) => kind switch
    {
        MailDnsCheckKind.Spf => "SPF",
        MailDnsCheckKind.Dkim => "DKIM",
        MailDnsCheckKind.Dmarc => "DMARC",
        MailDnsCheckKind.Ptr => "PTR",
        MailDnsCheckKind.BulkSender => "Bulk sender",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The status chip text. The status is always text, never colour alone.</summary>
    public static string DnsStatus(MailDnsStatus status) => status switch
    {
        MailDnsStatus.Pass => "Pass",
        MailDnsStatus.Warn => "Warn",
        MailDnsStatus.Fail => "Fail",
        MailDnsStatus.NotApplicable => "N/A",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
