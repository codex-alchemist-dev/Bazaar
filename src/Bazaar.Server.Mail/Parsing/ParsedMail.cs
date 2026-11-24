namespace Bazaar.Server.Mail.Parsing;

/// <summary>What the screens and the pipeline need from one message. Bodies stay in memory only until encrypted.</summary>
public sealed record ParsedMail(
    string? MessageId,
    string? FromAddress,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<MailAttachment> Attachments,
    bool AutoSubmitted,
    int PartCount,
    int MaxDepthSeen,
    IReadOnlyDictionary<string, string> AuthenticationResults);

/// <summary>One attachment as declared by the sender. Content is not decoded here.</summary>
public sealed record MailAttachment(string? FileName, string MediaType, long SizeBytes);

/// <summary>Raised when a message breaks a hard limit. The message is held, never partly processed.</summary>
public sealed class MailLimitException(string limit, string message) : Exception(message)
{
    /// <summary>Which limit tripped: <c>bytes</c>, <c>depth</c>, <c>parts</c> or <c>attachments</c>.</summary>
    public string Limit { get; } = limit;
}
