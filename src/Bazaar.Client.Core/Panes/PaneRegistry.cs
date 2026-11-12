using Avalonia.Controls;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Maps pane kinds to view-model factories. Unknown kinds and malformed models render the generic
/// tree view and report the kind once, so a newer server never breaks an older client.
/// </summary>
public sealed class PaneRegistry
{
    private readonly Dictionary<string, Func<PaneDescriptor, IPaneViewModel>> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Func<IPaneViewModel, Control>> _views = [];
    private readonly List<string> _kinds = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Action<string>? _report;

    /// <param name="report">Called once per kind that falls back. Receives the kind and the reason.</param>
    public PaneRegistry(Action<string>? report = null)
    {
        _report = report;
    }

    /// <summary>
    /// Builds a registry from <paramref name="modules"/>, in the order given. Each module registers its own kind, so
    /// a head composes its pane set from a list instead of naming each pane. Throws when two modules share a kind.
    /// </summary>
    /// <param name="modules">The pane modules to register.</param>
    /// <param name="context">The optional dependencies the modules read. Null uses an empty context.</param>
    public static PaneRegistry Compose(IEnumerable<IPaneModule> modules, PaneModuleContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(modules);

        context ??= new PaneModuleContext();
        var registry = new PaneRegistry(context.Report);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            module.Register(registry, context);
        }

        return registry;
    }

    /// <summary>Registers a factory. Throws <see cref="InvalidOperationException"/> when the kind is already registered.</summary>
    public void Register(string kind, Func<PaneDescriptor, IPaneViewModel> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(kind, factory))
        {
            throw new InvalidOperationException($"Pane kind '{kind}' is already registered.");
        }

        _kinds.Add(kind);
    }

    /// <summary>Registered kinds in registration order. The phone pager lists panes in this order (doc 29).</summary>
    public IReadOnlyList<string> Kinds => _kinds;

    /// <summary>True when a factory is registered for the kind.</summary>
    public bool Contains(string kind) => _factories.ContainsKey(kind);

    /// <summary>Builds the view model for a descriptor. Never throws for an unknown kind or a bad model.</summary>
    public IPaneViewModel Create(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!_factories.TryGetValue(descriptor.Kind, out var factory))
        {
            return Fallback(descriptor, "unknown kind");
        }

        try
        {
            return factory(descriptor);
        }
        catch (PaneModelException ex)
        {
            return Fallback(descriptor, ex.Message);
        }
    }

    /// <summary>
    /// Registers the view for one pane view model type. A module calls this next to <see cref="Register"/>, so the view
    /// ships with the module and Client.Core does not need to know the type.
    /// </summary>
    /// <param name="view">Builds the pane body. The registry adds the title above it.</param>
    /// <exception cref="InvalidOperationException">A view for <typeparamref name="TPane"/> is already registered.</exception>
    public void RegisterView<TPane>(Func<TPane, Control> view)
        where TPane : class, IPaneViewModel
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!_views.TryAdd(typeof(TPane), pane => view((TPane)pane)))
        {
            throw new InvalidOperationException($"A view for '{typeof(TPane).Name}' is already registered.");
        }
    }

    /// <summary>
    /// Builds the view for <paramref name="pane"/>: the registered view for its type under the pane title, or the
    /// generic view when no module registered one.
    /// </summary>
    public Control CreateView(IPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        return _views.TryGetValue(pane.GetType(), out var view)
            ? PaneViews.WithTitle(pane.Title, view(pane))
            : PaneViews.Create(pane);
    }

    private GenericPaneViewModel Fallback(PaneDescriptor descriptor, string reason)
    {
        if (_report is not null && _reported.Add(descriptor.Kind))
        {
            _report($"Pane '{descriptor.Kind}' rendered generically: {reason}.");
        }

        return new GenericPaneViewModel(descriptor);
    }
}
