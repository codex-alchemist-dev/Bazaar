using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Calendar.Panes;

/// <summary>Registers the company calendar pane.</summary>
public sealed class CalendarPaneModule : IPaneModule, IClientModule
{
    /// <summary>The pane kind; equals <c>PaneKinds.Calendar</c> once Bazaar.Contracts carries it.</summary>
    public const string PaneKind = Bazaar.Contracts.Panes.PaneKinds.Calendar;

    private readonly CalendarDayMapper? _mapper;

    public CalendarPaneModule(CalendarDayMapper? mapper = null)
    {
        _mapper = mapper;
    }

    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(CalendarPaneModule), PaneKind, [PaneKind], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKind;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKind, descriptor => new CalendarPaneViewModel(descriptor, _mapper));
        registry.RegisterView<CalendarPaneViewModel>(CalendarPaneViews.Calendar);
    }
}
