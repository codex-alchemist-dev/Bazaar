using System.ComponentModel;
using Bazaar.Client.Core.Constellation;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation;

/// <summary>
/// Read-only view model for the phone's Constellation drawer (doc 29, work package 10; doc 19 tile feed). It asks the tile
/// source for the nodes inside a box and lists them, and it opens one listed node. It never edits, sketches or commits.
/// Each refresh gets a generation number, so a response from an older refresh is dropped, and a frame is dropped when its
/// graph version, view, node count or layout sequence does not fit the newest state. The model holds no Avalonia types.
/// </summary>
public sealed class ConstellationDrawerViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// The most nodes one drawer listing holds. A phone list is shorter than the 2D pane's cap, which is why it is not the pane's
    /// <c>DefaultNodeCap</c>. Operator setting candidate <c>android.constellation_drawer_node_cap</c> (doc 29 settings table, not
    /// yet registered); until then the head passes its value through the constructor.
    /// </summary>
    public const int DefaultNodeCap = 200;

    private readonly IConstellationTileSource _tiles;
    private readonly object _gate = new();
    private int _generation;
    private long? _graphVersion;
    private long _lastLayoutSeq = -1;
    private DrawerStatus _status = DrawerStatus.NoGraph;
    private DrawerNode[] _nodes = [];
    private DrawerNode? _opened;

    /// <param name="tiles">Where the node frames come from. A head injects the HTTP source; tests inject a fake.</param>
    /// <param name="nodeCap">The most nodes one listing may hold. Frames above it are dropped.</param>
    public ConstellationDrawerViewModel(IConstellationTileSource tiles, int nodeCap = DefaultNodeCap)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeCap);
        _tiles = tiles;
        NodeCap = nodeCap;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The most nodes one listing holds, and the cap sent with every request.</summary>
    public int NodeCap { get; }

    public DrawerStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>The listed nodes, in the order the server sent them. Empty unless <see cref="Status"/> is Ready.</summary>
    public IReadOnlyList<DrawerNode> Nodes
    {
        get { lock (_gate) return _nodes; }
    }

    /// <summary>The node the user opened, or null. It is cleared when a refresh no longer lists that node.</summary>
    public DrawerNode? OpenedNode
    {
        get { lock (_gate) return _opened; }
    }

    /// <summary>
    /// Lists the nodes inside <paramref name="box"/>. A null <paramref name="graph"/> means no LOD snapshot yet, and nothing is
    /// requested. A graph above the node cap (<see cref="DrawerGraphState.ClustersOnly"/>) is not requested either. Cancellation
    /// and transport errors from the tile source propagate to the caller.
    /// </summary>
    public async Task RefreshAsync(ConstellationBox box, DrawerGraphState? graph, CancellationToken cancellationToken = default)
    {
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
        }

        if (graph is null)
        {
            Clear(generation, DrawerStatus.NoGraph);
            return;
        }

        if (graph.ClustersOnly)
        {
            Clear(generation, DrawerStatus.ClustersOnly);
            return;
        }

        var request = new ViewportRequest(
            viewId: PaneKinds.Constellation,
            graphVersion: graph.GraphVersion,
            tier: Bazaar.Contracts.Graph.ZoomTier.Near,
            minX: box.MinX,
            minY: box.MinY,
            minZ: 0,
            maxX: box.MaxX,
            maxY: box.MaxY,
            maxZ: 0,
            maxNodes: NodeCap);

        TileFrame? frame = await _tiles.GetFrameAsync(request, cancellationToken);
        Accept(generation, request, frame);
    }

    /// <summary>Opens a listed node. Returns false, and changes nothing, when no listed node has that id.</summary>
    public bool OpenNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate)
        {
            var node = _nodes.FirstOrDefault(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal));
            if (node is null)
            {
                return false;
            }

            _opened = node;
        }

        Notify(nameof(OpenedNode));
        return true;
    }

    /// <summary>Closes the opened node, if any.</summary>
    public void Close()
    {
        bool changed;
        lock (_gate)
        {
            changed = _opened is not null;
            _opened = null;
        }

        if (changed)
        {
            Notify(nameof(OpenedNode));
        }
    }

    private void Clear(int generation, DrawerStatus status)
    {
        bool changed;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            changed = _status != status || _nodes.Length > 0 || _opened is not null;
            _graphVersion = null;
            _lastLayoutSeq = -1;
            _status = status;
            _nodes = [];
            _opened = null;
        }

        if (changed)
        {
            Notify(nameof(Status), nameof(Nodes), nameof(OpenedNode));
        }
    }

    /// <summary>Applies a tile response unless it is superseded, from another view or graph, over the cap, or out of order.</summary>
    private void Accept(int generation, ViewportRequest request, TileFrame? frame)
    {
        bool changed;
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            if (frame is not null &&
                (frame.GraphVersion != request.GraphVersion || frame.ViewId != request.ViewId || frame.NodeIds.Count > request.MaxNodes))
            {
                return;
            }

            if (_graphVersion != request.GraphVersion)
            {
                _graphVersion = request.GraphVersion;
                _lastLayoutSeq = -1;
            }

            if (frame is not null && frame.LayoutSeq < _lastLayoutSeq)
            {
                return;
            }

            DrawerNode[] nodes = frame is null ? [] : ToNodes(frame);
            if (frame is not null)
            {
                _lastLayoutSeq = frame.LayoutSeq;
            }

            var status = nodes.Length == 0 ? DrawerStatus.Empty : DrawerStatus.Ready;
            changed = _status != status || !SameNodes(_nodes, nodes);
            _nodes = nodes;
            _status = status;
            if (_opened is not null && !nodes.Any(n => string.Equals(n.Id, _opened.Id, StringComparison.Ordinal)))
            {
                _opened = null;
                changed = true;
            }
        }

        if (changed)
        {
            Notify(nameof(Status), nameof(Nodes), nameof(OpenedNode));
        }
    }

    private static DrawerNode[] ToNodes(TileFrame frame)
    {
        var span = frame.Xyz.Span;
        var nodes = new DrawerNode[frame.NodeIds.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new DrawerNode(frame.NodeIds[i], span[i * 3], span[(i * 3) + 1]);
        }

        return nodes;
    }

    private static bool SameNodes(DrawerNode[] a, DrawerNode[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private void Notify(params string[] properties)
    {
        foreach (var property in properties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
