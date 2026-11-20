using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Calendar;

/// <summary>
/// The one GC write the Company Calendar needs: appending the announcement line after a committed change (docs/design/24
/// section 4). It is a port so the calendar module does not reference the GC writer, which still lives in Bazaar.Server. The
/// host adapts <c>IGcLogWriter</c> to this interface when it composes the calendar.
/// </summary>
public interface ICalendarGcAppender
{
    /// <summary>Appends one GC event. Same semantics as <c>IGcLogWriter.AppendAsync</c>: idempotent by the request's client message id.</summary>
    Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct);
}
