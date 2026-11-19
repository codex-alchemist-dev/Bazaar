using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Calendar;

/// <summary>Adapts a delegate to <see cref="ICalendarGcAppender"/>, so the host can supply the GC write without a calendar-side adapter type.</summary>
public sealed class DelegateCalendarGcAppender(Func<AppendRequest, CancellationToken, Task<AppendResult>> append) : ICalendarGcAppender
{
    private readonly Func<AppendRequest, CancellationToken, Task<AppendResult>> _append = append ?? throw new ArgumentNullException(nameof(append));

    /// <inheritdoc />
    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct) => _append(request, ct);
}
