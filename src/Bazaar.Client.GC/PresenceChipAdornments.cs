using Avalonia.Controls;
using Avalonia.Threading;
using Bazaar.Client.GC.Abstractions;

namespace Bazaar.Client.GC;

/// <summary>
/// The strip of adornments on one agent's presence chip. Renders each registered adornment's control in order, shows no
/// slot for an adornment that returns null, and asks again when the registry grows or an adornment raises <c>Changed</c>
/// for this agent. The presence dot and label are not part of this control.
/// </summary>
public sealed class PresenceChipAdornments : StackPanel, IDisposable
{
    private readonly IPresenceAdornmentRegistry _registry;
    private readonly Guid _agentId;
    private readonly Action<Guid> _onChanged;
    private readonly Action _onAdded;
    private readonly List<IPresenceChipAdornment> _subscribed = [];

    /// <param name="registry">The adornments to show.</param>
    /// <param name="agentId">The agent the chip belongs to.</param>
    public PresenceChipAdornments(IPresenceAdornmentRegistry registry, Guid agentId)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _agentId = agentId;
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        Spacing = 4;
        _onChanged = id =>
        {
            if (id == _agentId) Post(Rebuild);
        };
        _onAdded = () => Post(Rebuild);
        _registry.Added += _onAdded;
        Rebuild();
    }

    /// <summary>Asks every adornment again and rebuilds the strip. Call on the UI thread.</summary>
    public void Rebuild()
    {
        Children.Clear();
        foreach (IPresenceChipAdornment adornment in _registry.Adornments)
        {
            if (!_subscribed.Contains(adornment))
            {
                adornment.Changed += _onChanged;
                _subscribed.Add(adornment);
            }

            Control? control;
            try
            {
                control = adornment.Create(_agentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken adornment must not take the chip down; it renders no slot.
                control = null;
            }

            if (control is not null) Children.Add(control);
        }

        IsVisible = Children.Count > 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _registry.Added -= _onAdded;
        foreach (IPresenceChipAdornment adornment in _subscribed) adornment.Changed -= _onChanged;
        _subscribed.Clear();
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
