namespace Bazaar.Client.Core.Panes;

/// <summary>
/// One pane kind a client ships with, as a unit that registers itself. A feature adds its pane by implementing this
/// interface and passing it to <see cref="PaneRegistry.Compose"/>; dropping the pane means leaving it out of the list.
/// </summary>
public interface IPaneModule
{
    /// <summary>The module's identifier. First-party modules use the pane kind they register.</summary>
    string Id { get; }

    /// <summary>Registers this module's pane kind, with its factory, on <paramref name="registry"/>.</summary>
    void Register(PaneRegistry registry, PaneModuleContext context);
}
