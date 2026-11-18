using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Calendar.Panes;

/// <summary>Registers the company calendar pane.</summary>
public sealed class CalendarPaneModule : IPaneModule
{
    /// <summary>The pane kind; equals <c>PaneKinds.Calendar</c> once Bazaar.Contracts carries it.</summary>
    public const string PaneKind = "bazaar.calendar";

    private readonly CalendarDayMapper? _mapper;

    public CalendarPaneModule(CalendarDayMapper? mapper = null)
    {
        _mapper = mapper;
    }

    public string Id => PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKind, descriptor => new CalendarPaneViewModel(descriptor, _mapper));
        registry.RegisterView<CalendarPaneViewModel>(CalendarPaneViews.Calendar);
    }
}
