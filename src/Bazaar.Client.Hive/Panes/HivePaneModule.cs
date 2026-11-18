using Bazaar.Client.Core.Panes;
using Bazaar.Client.Hive.Grants;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Hive.Panes;

/// <summary>Registers the Hive pane (<see cref="PaneKinds.Hive"/>). The grant actions come from the head's context, if any.</summary>
public sealed class HivePaneModule : IPaneModule
{
    public string Id => PaneKinds.Hive;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        var actions = context.Get<IHiveGrantActions>();
        registry.Register(PaneKinds.Hive, descriptor => new HivePaneViewModel(descriptor, actions));
        registry.RegisterView<HivePaneViewModel>(HivePaneViews.Hive);
    }
}
