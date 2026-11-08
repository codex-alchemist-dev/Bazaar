using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// Maps pane kinds to view-model factories. Unknown kinds and malformed models render the generic
/// tree view and report the kind once, so a newer server never breaks an older client.
/// </summary>
public sealed class PaneRegistry
{
    private readonly Dictionary<string, Func<PaneDescriptor, IPaneViewModel>> _factories = new(StringComparer.Ordinal);
    private readonly List<string> _kinds = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Action<string>? _report;

    /// <param name="report">Called once per kind that falls back. Receives the kind and the reason.</param>
    public PaneRegistry(Action<string>? report = null)
    {
        _report = report;
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

    private GenericPaneViewModel Fallback(PaneDescriptor descriptor, string reason)
    {
        if (_report is not null && _reported.Add(descriptor.Kind))
        {
            _report($"Pane '{descriptor.Kind}' rendered generically: {reason}.");
        }

        return new GenericPaneViewModel(descriptor);
    }
}
