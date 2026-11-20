using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Calendar.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Calendar;

/// <summary>The Calendar client module: the pane modules a head composes to ship the company calendar.</summary>
public static class CalendarPanes
{
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new CalendarPaneModule(),
    ];
}
