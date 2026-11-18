using System.Globalization;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Calendar.Panes;

/// <summary>
/// The company calendar pane (doc 24 section 4): a read-only month grid with one chip per event per day it covers.
/// Model: <c>{ "displayTimezone": "viewer"|"agent", "events": [ { id, kind, title, startsAt, endsAt, allDay, tz,
/// agentId|null, projectId|null, affectsAvailability, status } ] }</c>. Malformed data raises
/// <see cref="PaneModelException"/>. Titles are personal fields and are never logged.
/// </summary>
public sealed class CalendarPaneViewModel : IPaneViewModel
{
    private readonly CalendarSnapshot _snapshot;
    private readonly CalendarDayMapper _mapper;

    public CalendarPaneViewModel(PaneDescriptor descriptor, CalendarDayMapper? mapper = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Calendar";
        _snapshot = CalendarModelReader.Read(descriptor.Model);
        _mapper = mapper ?? new CalendarDayMapper();
        var today = _mapper.Today;
        VisibleMonth = new DateOnly(today.Year, today.Month, 1);
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>First day of the month on show.</summary>
    public DateOnly VisibleMonth { get; private set; }

    public string MonthLabel => VisibleMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public CalendarDisplayTimezone DisplayTimezone => _snapshot.DisplayTimezone;

    public void NextMonth() => VisibleMonth = VisibleMonth.AddMonths(1);

    public void PreviousMonth() => VisibleMonth = VisibleMonth.AddMonths(-1);

    public void GoToToday()
    {
        var today = _mapper.Today;
        VisibleMonth = new DateOnly(today.Year, today.Month, 1);
    }

    /// <summary>The weeks of the visible month, Monday first, padded with the neighbouring months' days.</summary>
    public IReadOnlyList<IReadOnlyList<CalendarDayCell>> BuildWeeks()
    {
        var offset = ((int)VisibleMonth.DayOfWeek + 6) % 7;
        var gridStart = VisibleMonth.AddDays(-offset);
        var lastOfMonth = VisibleMonth.AddMonths(1).AddDays(-1);
        var weekCount = ((lastOfMonth.DayNumber - gridStart.DayNumber) / 7) + 1;
        var today = _mapper.Today;

        var ranges = _snapshot.Events
            .Select(e => (Item: e, Range: _mapper.DatesCovered(e, _snapshot.DisplayTimezone)))
            .ToArray();

        var weeks = new List<IReadOnlyList<CalendarDayCell>>(weekCount);
        for (var w = 0; w < weekCount; w++)
        {
            var days = new List<CalendarDayCell>(7);
            for (var d = 0; d < 7; d++)
            {
                var date = gridStart.AddDays((w * 7) + d);
                var chips = ranges
                    .Where(r => r.Range.First <= date && date <= r.Range.Last)
                    .Select(r => r.Item)
                    .OrderByDescending(e => e.AllDay)
                    .ThenBy(e => e.StartsAt)
                    .ThenBy(e => e.Id, StringComparer.Ordinal)
                    .Select(e => new CalendarChip(e))
                    .ToArray();
                days.Add(new CalendarDayCell(date, date.Month == VisibleMonth.Month && date.Year == VisibleMonth.Year, date == today, chips));
            }

            weeks.Add(days);
        }

        return weeks;
    }
}

/// <summary>One day in the month grid.</summary>
public sealed record CalendarDayCell(DateOnly Date, bool InVisibleMonth, bool IsToday, IReadOnlyList<CalendarChip> Chips);

/// <summary>
/// One event chip. <see cref="Label"/> and <see cref="AccessibleName"/> carry kind and status in text, so colour is
/// never the only signal. A cancelled event stays on the grid, struck through.
/// </summary>
public sealed class CalendarChip
{
    public CalendarChip(CalendarEventSummary item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EventId = item.Id;
        IsCancelled = item.Status == CalendarEventStatus.Cancelled;
        KindLabel = item.Kind.ToString();
        StatusLabel = item.Status.ToString();
        var statusSuffix = item.Status == CalendarEventStatus.Confirmed ? string.Empty : $" ({StatusLabel.ToLowerInvariant()})";
        Label = $"{KindLabel}: {item.Title}{statusSuffix}";
        AccessibleName = $"{KindLabel}, {StatusLabel}: {item.Title}";
    }

    public string EventId { get; }

    public bool IsCancelled { get; }

    public string KindLabel { get; }

    public string StatusLabel { get; }

    public string Label { get; }

    public string AccessibleName { get; }
}
