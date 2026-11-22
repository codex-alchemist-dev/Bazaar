using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Plays the droplet bubble on messages that arrive in a list while it is on screen (doc 93, catalogue rows "New message bubble" and
/// "Own message sent"). Only items added to the bound collection after <see cref="Attach"/> are candidates, so the first load of a
/// list animates nothing, and a container that is recycled for an old item by scrolling never animates: an item is marked when it is
/// added and unmarked the first time it gets a container (or at the end of the layout pass). A burst of more than
/// <see cref="MotionHost.MaxAnimatedBurst"/> new items in one pass is a history load, not live arrivals, and is not animated.
/// </summary>
public sealed class MessageListMotion : IDisposable
{
    private readonly ListBox _list;
    private readonly Func<object, DropletKind> _classify;
    private readonly MotionSettings _settings;
    private readonly Func<Control, IFrameDriver> _driverFactory;
    private readonly HashSet<object> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<Control, DropletBubbleAnimator> _animators = [];
    private INotifyCollectionChanged? _source;
    private int _burst;
    private bool _flushQueued;

    private MessageListMotion(ListBox list, Func<object, DropletKind> classify, MotionSettings settings, Func<Control, IFrameDriver> driverFactory)
    {
        _list = list;
        _classify = classify;
        _settings = settings;
        _driverFactory = driverFactory;
        list.PropertyChanged += OnListPropertyChanged;
        list.ContainerPrepared += OnContainerPrepared;
        list.ContainerClearing += OnContainerClearing;
        Rebind();
    }

    /// <summary>Starts watching <paramref name="list"/>.</summary>
    /// <param name="list">The message list.</param>
    /// <param name="classify">Says whether an item is the person's own message; defaults to "someone else's".</param>
    /// <param name="settings">Defaults to <see cref="MotionHost.Settings"/>.</param>
    /// <param name="driverFactory">Defaults to <see cref="MotionHost.DriverFor"/>; tests pass a manual driver.</param>
    public static MessageListMotion Attach(
        ListBox list,
        Func<object, DropletKind>? classify = null,
        MotionSettings? settings = null,
        Func<Control, IFrameDriver>? driverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new MessageListMotion(list, classify ?? (_ => DropletKind.Arrival), settings ?? MotionHost.Settings, driverFactory ?? MotionHost.DriverFor);
    }

    public void Dispose()
    {
        _list.PropertyChanged -= OnListPropertyChanged;
        _list.ContainerPrepared -= OnContainerPrepared;
        _list.ContainerClearing -= OnContainerClearing;
        Unhook();
    }

    private void OnListPropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty)
        {
            Rebind();
        }
    }

    // A new source is a new baseline: whatever it already holds is loaded history.
    private void Rebind()
    {
        Unhook();
        _pending.Clear();
        _burst = 0;
        _source = _list.ItemsSource as INotifyCollectionChanged;
        if (_source is not null)
        {
            _source.CollectionChanged += OnCollectionChanged;
        }
    }

    private void Unhook()
    {
        if (_source is not null)
        {
            _source.CollectionChanged -= OnCollectionChanged;
            _source = null;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _pending.Clear();
            return;
        }

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (object? item in (IEnumerable)e.NewItems)
        {
            if (item is not null)
            {
                _pending.Add(item);
                _burst++;
            }
        }

        if (!_flushQueued)
        {
            _flushQueued = true;
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    // Layout has run by now: items that never got a container (scrolled out of view) must not animate when they are scrolled to later.
    private void Flush()
    {
        _flushQueued = false;
        _pending.Clear();
        _burst = 0;
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // The container's data context is not set yet at this point, so the item comes from its index.
        var item = e.Index >= 0 && e.Index < _list.ItemCount ? _list.Items[e.Index] : null;
        if (item is null || !_pending.Remove(item) || _burst > MotionHost.MaxAnimatedBurst)
        {
            return;
        }

        var container = e.Container;
        Release(container);
        var animator = new DropletBubbleAnimator(container, FindText(container), _settings, _driverFactory(container));
        animator.Completed += (_, _) => Release(container);
        _animators.AddOrUpdate(container, animator);

        var kind = _classify(item);
        var own = kind == DropletKind.OwnSent;
        animator.Play(kind, own ? 0 : MotionHost.ArrivalOriginDx, own ? MotionHost.OwnOriginDy : MotionHost.ArrivalOriginDy);
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (_animators.TryGetValue(e.Container, out var animator))
        {
            animator.Interrupt();
        }

        Release(e.Container);
    }

    private void Release(Control container)
    {
        if (_animators.TryGetValue(container, out var animator))
        {
            animator.Dispose();
            _animators.Remove(container);
        }
    }

    // The text fades in after the shape settles; the template may not be applied yet when the container is prepared.
    private static TextBlock? FindText(Control container)
    {
        if (container is ContentControl cc)
        {
            container.ApplyTemplate();
            cc.Presenter?.ApplyTemplate();
        }

        return container.GetVisualDescendants().OfType<TextBlock>().LastOrDefault();
    }
}
