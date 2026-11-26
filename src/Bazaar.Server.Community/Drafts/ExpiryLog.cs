using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Community.Drafts;

/// <summary>Source-generated log messages for the draft expiry sweeper. Ids and counts only, never a body.</summary>
internal static partial class ExpiryLog
{
    public static void RowNotWritten(ILogger? logger, Exception ex, Guid draftId)
    {
        if (logger is not null) RowNotWrittenCore(logger, ex, draftId);
    }

    public static void Unrecorded(ILogger? logger, int count)
    {
        if (logger is not null) UnrecordedCore(logger, count);
    }

    public static void Expired(ILogger? logger, int count)
    {
        if (logger is not null) ExpiredCore(logger, count);
    }

    public static void SweepFailed(ILogger? logger, Exception ex)
    {
        if (logger is not null) SweepFailedCore(logger, ex);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Community draft {DraftId} expired but its action-log row was not written.")]
    private static partial void RowNotWrittenCore(ILogger logger, Exception ex, Guid draftId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} expired community drafts have no action-log row.")]
    private static partial void UnrecordedCore(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} community drafts past their approval window.")]
    private static partial void ExpiredCore(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Community draft expiry sweep failed; retrying at the next interval.")]
    private static partial void SweepFailedCore(ILogger logger, Exception ex);
}
