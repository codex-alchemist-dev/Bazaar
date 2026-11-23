using System.Text;

namespace Bazaar.Server.MailDns;

/// <summary>
/// Hard caps for DNS text. A DNS answer is untrusted input: the resolver and the checker both cap what they take, and any DNS
/// value that reaches a message is reduced to a safe character set first, so it can never act as markup or as an instruction.
/// </summary>
public static class MailDnsLimits
{
    /// <summary>Most records taken from one answer.</summary>
    public const int MaxRecords = 32;

    /// <summary>Longest TXT value kept, in characters. Longer values are cut.</summary>
    public const int MaxTxtLength = 4096;

    /// <summary>SPF lookup limit (RFC 7208 section 4.6.4).</summary>
    public const int MaxSpfLookups = 10;

    /// <summary>Deepest include and redirect chain followed.</summary>
    public const int MaxSpfDepth = 10;

    /// <summary>Gmail bulk-sender threshold: messages a day [RESEARCHED, doc 22].</summary>
    public const int BulkSenderDailyThreshold = 5000;

    private const int _maxShownLength = 80;

    /// <summary>Cuts <paramref name="value"/> to <see cref="MaxTxtLength"/>.</summary>
    public static string CapTxt(string value) => value.Length <= MaxTxtLength ? value : value[..MaxTxtLength];

    /// <summary>Takes at most <see cref="MaxRecords"/> TXT values and cuts each to <see cref="MaxTxtLength"/>.</summary>
    public static IReadOnlyList<string> ShapeTxt(IEnumerable<string> values) => [.. values.Take(MaxRecords).Select(CapTxt)];

    /// <summary>
    /// A DNS-derived name or value made safe to show: only letters, digits, dot, dash, underscore and colon survive, anything else
    /// becomes a question mark, and the length is capped.
    /// </summary>
    public static string Safe(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, _maxShownLength));
        foreach (var c in value)
        {
            if (builder.Length >= _maxShownLength)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' ? c : '?');
        }

        return builder.ToString();
    }
}
