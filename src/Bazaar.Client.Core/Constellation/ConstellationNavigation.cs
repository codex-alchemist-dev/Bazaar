namespace Bazaar.Client.Core.Constellation;

/// <summary>Level of detail for the Constellation view (19, "Client renderer").</summary>
public enum ZoomTier
{
    Far,
    Mid,
    Near,
}

/// <summary>Navigation commands the Constellation understands. Keys map to these through <see cref="ConstellationKeymap"/>.</summary>
public enum ConstellationCommandKind
{
    Focus,
    Back,
    Forward,
    NextNeighbour,
    PreviousNeighbour,
    ZoomIn,
    ZoomOut,
}

/// <summary>A command plus its node argument. <see cref="NodeId"/> is required for <see cref="ConstellationCommandKind.Focus"/> only.</summary>
public readonly record struct ConstellationCommand(ConstellationCommandKind Kind, string? NodeId = null);

/// <summary>
/// The client-side shape of a FocusRequest (19): the view and the node to focus. Plain strings, not Contracts types.
/// The caller sends it to the server over the socket.
/// </summary>
public sealed record ConstellationFocusRequest(string ViewId, string NodeId);

/// <summary>
/// Pure navigation model for one Constellation view. No UI: each command returns the focus changes it caused,
/// so a test or a head can replay a key sequence and check the result.
/// </summary>
/// <remarks>
/// Focus history behaves like a browser stack. A new focus pushes the old node onto the back stack and clears the
/// forward stack. Back and Forward move between the stacks and do not clear either one. Focusing the node that is
/// already focused does nothing.
/// Neighbour steps walk the focused node's neighbours. The first step starts a walk from the node focused at that
/// moment: Next goes to the first neighbour in ordinal order, Previous to the last. Later steps continue around that
/// same ring, wrapping. Any Focus, Back or Forward ends the walk. Each step is a focus change and enters the history.
/// Zoom changes move through Far, Mid and Near and clamp at the ends. They emit no focus change.
/// </remarks>
public sealed class ConstellationNavigation
{
    private const int DefaultHistoryLimit = 200;

    private readonly string _viewId;
    private readonly int _historyLimit;

    // Stacks: the top (most recent) is the last element.
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    private string? _focused;

    // Neighbour walk state. Null anchor means no walk is active.
    private string? _walkAnchor;
    private string[] _walkRing = [];
    private int _walkIndex;

    /// <summary>Creates a navigation model for one view.</summary>
    /// <param name="viewId">The view id copied into every focus request.</param>
    /// <param name="historyLimit">Maximum entries kept on each stack; the oldest are dropped first.</param>
    public ConstellationNavigation(string viewId, int historyLimit = DefaultHistoryLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLimit, 1);
        _viewId = viewId;
        _historyLimit = historyLimit;
    }

    /// <summary>Focused node id, or null before the first focus.</summary>
    public string? FocusedNodeId => _focused;

    /// <summary>Current zoom tier. Starts at <see cref="ZoomTier.Far"/>.</summary>
    public ZoomTier Zoom { get; private set; } = ZoomTier.Far;

    /// <summary>Entries on the back stack.</summary>
    public int BackDepth => _back.Count;

    /// <summary>Entries on the forward stack.</summary>
    public int ForwardDepth => _forward.Count;

    /// <summary>
    /// Runs one command and returns the focus requests it produced (empty when nothing changed).
    /// <paramref name="adjacency"/> supplies the neighbours of each node for the neighbour commands; it may be null otherwise.
    /// </summary>
    public IReadOnlyList<ConstellationFocusRequest> Apply(
        ConstellationCommand command,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? adjacency = null)
    {
        return command.Kind switch
        {
            ConstellationCommandKind.Focus when command.NodeId is null => [],
            ConstellationCommandKind.Focus => Focus(command.NodeId!),
            ConstellationCommandKind.Back => Back(),
            ConstellationCommandKind.Forward => Forward(),
            ConstellationCommandKind.NextNeighbour => StepNeighbour(adjacency, 1),
            ConstellationCommandKind.PreviousNeighbour => StepNeighbour(adjacency, -1),
            ConstellationCommandKind.ZoomIn => ZoomBy(1),
            ConstellationCommandKind.ZoomOut => ZoomBy(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown Constellation command."),
        };
    }

    /// <summary>Focuses a node, pushing the current focus onto the back stack and clearing the forward stack.</summary>
    public IReadOnlyList<ConstellationFocusRequest> Focus(string nodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        EndWalk();
        return MoveTo(nodeId);
    }

    /// <summary>Moves to the previous focus. Returns empty when the back stack is empty.</summary>
    public IReadOnlyList<ConstellationFocusRequest> Back()
    {
        EndWalk();
        if (_back.Count == 0)
        {
            return [];
        }

        string previous = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_focused is not null)
        {
            PushBounded(_forward, _focused);
        }

        _focused = previous;
        return [new ConstellationFocusRequest(_viewId, previous)];
    }

    /// <summary>Moves to the focus that was left by <see cref="Back"/>. Returns empty when the forward stack is empty.</summary>
    public IReadOnlyList<ConstellationFocusRequest> Forward()
    {
        EndWalk();
        if (_forward.Count == 0)
        {
            return [];
        }

        string next = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_focused is not null)
        {
            PushBounded(_back, _focused);
        }

        _focused = next;
        return [new ConstellationFocusRequest(_viewId, next)];
    }

    /// <summary>Sets the zoom tier directly. Emits no focus change.</summary>
    public void SetZoom(ZoomTier tier)
    {
        Zoom = tier;
    }

    private IReadOnlyList<ConstellationFocusRequest> MoveTo(string target)
    {
        if (target == _focused)
        {
            return [];
        }

        if (_focused is not null)
        {
            PushBounded(_back, _focused);
        }

        _forward.Clear();
        _focused = target;
        return [new ConstellationFocusRequest(_viewId, target)];
    }

    private IReadOnlyList<ConstellationFocusRequest> StepNeighbour(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? adjacency,
        int direction)
    {
        if (_focused is null)
        {
            return [];
        }

        if (_walkAnchor is null)
        {
            if (adjacency is null || !adjacency.TryGetValue(_focused, out IReadOnlyList<string>? neighbours))
            {
                return [];
            }

            // Ordinal order, self excluded, so the ring is the same however the caller ordered the list.
            string[] ring = neighbours
                .Where(id => id != _focused)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (ring.Length == 0)
            {
                return [];
            }

            _walkAnchor = _focused;
            _walkRing = ring;

            // The first step lands on the first neighbour (Next) or the last one (Previous).
            _walkIndex = direction > 0 ? -1 : 0;
        }

        int count = _walkRing.Length;
        _walkIndex = ((_walkIndex + direction) % count + count) % count;
        return MoveTo(_walkRing[_walkIndex]);
    }

    private IReadOnlyList<ConstellationFocusRequest> ZoomBy(int steps)
    {
        int next = Math.Clamp((int)Zoom + steps, (int)ZoomTier.Far, (int)ZoomTier.Near);
        Zoom = (ZoomTier)next;
        return [];
    }

    private void EndWalk()
    {
        _walkAnchor = null;
        _walkRing = [];
        _walkIndex = 0;
    }

    private void PushBounded(List<string> stack, string nodeId)
    {
        stack.Add(nodeId);
        if (stack.Count > _historyLimit)
        {
            stack.RemoveAt(0);
        }
    }
}
