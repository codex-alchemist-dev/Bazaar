using System.ComponentModel;
using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Constellation;

/// <summary>One node of the Constellation pane: its id, screen-reader label and kind.</summary>
public sealed record ConstellationNode(string Id, string Label, string Kind);

/// <summary>
/// View model for the Constellation 2D pane (doc 19). Model:
/// <c>{ "nodes": [ { "id", "label", "kind" } ], "edges": [ { "from", "to" } ] }</c>.
/// Layout frames come from the server's LOD feed through <see cref="ApplyFrame"/>. Visible nodes come from the tile endpoint
/// through <see cref="SetViewportAsync"/>, which keeps one request in flight and drops responses from an older graph version.
/// The model holds no Avalonia types.
/// </summary>
public sealed class ConstellationPaneViewModel : IPaneViewModel, INotifyPropertyChanged
{
    /// <summary>The node cap the viewer asks for. Mirrors the shipped default of <c>constellation.node_cap</c>; the contract has none.</summary>
    public const int DefaultNodeCap = 10000;

    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);
    private readonly FrameSequencer _sequencer = new();
    private readonly FrameInterpolator _interpolator = new();
    private readonly SpatialGrid _grid = new(cellSize: 32f);
    private readonly IConstellationTileSource? _tiles;
    private readonly object _gate = new();
    private double _animateUntilSeconds;

    // Tile state. _queued is the one pending request; _fetching is true while the fetch loop owns the source.
    private ViewportRequest? _queued;
    private bool _fetching;
    private long _graphVersion = -1;
    private bool _clustersOnly;
    private long _lastTileSeq = -1;
    private TileNode[] _tileNodes = [];

    public ConstellationPaneViewModel(PaneDescriptor descriptor)
        : this(descriptor, tiles: null, DefaultNodeCap)
    {
    }

    /// <param name="descriptor">The pane descriptor. Its model supplies the static nodes and edges.</param>
    /// <param name="tiles">Where viewport tiles come from. Null means the pane never requests tiles.</param>
    /// <param name="nodeCap">The most nodes a single tile frame may hold. Frames above it are dropped.</param>
    public ConstellationPaneViewModel(PaneDescriptor descriptor, IConstellationTileSource? tiles, int nodeCap)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeCap);

        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Navigation = new ConstellationNavigation(viewId: descriptor.Kind);
        Camera = new Camera2D();
        _tiles = tiles;
        NodeCap = nodeCap;

        var nodes = new List<ConstellationNode>();
        if (descriptor.Model.ValueKind == JsonValueKind.Object &&
            descriptor.Model.TryGetProperty("nodes", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodeArray.EnumerateArray())
            {
                if (TryReadNode(node, out var parsed) && !_indexById.ContainsKey(parsed.Id))
                {
                    _indexById[parsed.Id] = nodes.Count;
                    nodes.Add(parsed);
                }
            }
        }

        Nodes = nodes;

        var pairs = new List<int>();
        var skipped = 0;
        if (descriptor.Model.ValueKind == JsonValueKind.Object &&
            descriptor.Model.TryGetProperty("edges", out var edgeArray) && edgeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edgeArray.EnumerateArray())
            {
                if (edge.ValueKind == JsonValueKind.Object &&
                    edge.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String &&
                    edge.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String &&
                    _indexById.TryGetValue(from.GetString()!, out int a) &&
                    _indexById.TryGetValue(to.GetString()!, out int b))
                {
                    pairs.Add(a);
                    pairs.Add(b);
                }
                else
                {
                    skipped++;
                }
            }
        }

        SkippedEdgeCount = skipped;
        Scene = new ConstellationScene(nodes.Count, pairs.ToArray(), ReadOnlyMemory<int>.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<ConstellationNode> Nodes { get; }

    public ConstellationScene Scene { get; }

    /// <summary>Edges left out because an endpoint was unknown or malformed.</summary>
    public int SkippedEdgeCount { get; }

    public ConstellationNavigation Navigation { get; }

    public Camera2D Camera { get; }

    /// <summary>The newest layout positions, interpolated for drawing.</summary>
    public FrameInterpolator Interpolator => _interpolator;

    /// <summary>Hit-test grid over the current interpolation target. Rebuilt on each accepted frame.</summary>
    public SpatialGrid Grid => _grid;

    /// <summary>The most nodes one tile frame may hold, and the cap sent with every request.</summary>
    public int NodeCap { get; }

    /// <summary>The graph version the pane is showing, or null until a LOD snapshot has arrived.</summary>
    public long? GraphVersion
    {
        get { lock (_gate) return _graphVersion < 0 ? null : _graphVersion; }
    }

    /// <summary>True when the newest LOD snapshot says the graph is above the node cap. Nodes are then not requested.</summary>
    public bool ClustersOnly
    {
        get { lock (_gate) return _clustersOnly; }
    }

    /// <summary>Nodes of the last accepted tile frame, in graph coordinates. Empty when no tile is shown.</summary>
    public IReadOnlyList<TileNode> TileNodes
    {
        get { lock (_gate) return _tileNodes; }
    }

    /// <summary>True when the pane has something to draw: static nodes, a tile frame, or a LOD snapshot.</summary>
    public bool HasData
    {
        get
        {
            lock (_gate)
            {
                return Nodes.Count > 0 || _tileNodes.Length > 0 || _graphVersion >= 0;
            }
        }
    }

    public string? FocusedNodeId => Navigation.FocusedNodeId;

    /// <summary>The ordered list used by screen readers and keyboard-only users. Rebuilt on each focus change.</summary>
    public AccessibleNodeList AccessibleList => AccessibleNodeList.Create(
        Nodes.Select(n => new AccessibleNodeInput(n.Id, n.Label, n.Kind)), FocusedNodeId);

    /// <summary>True while a frame is still blending, so the view should keep redrawing.</summary>
    public bool IsAnimating(double nowSeconds) => nowSeconds < _animateUntilSeconds;

    /// <summary>
    /// Accepts a server layout frame. Rejects frames for a different node count, 3D frames (not drawn yet), and frames
    /// that are not newer than the last accepted one. Returns true when the frame was applied.
    /// </summary>
    public bool ApplyFrame(RenderFrame frame, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Dimensions != 2 || frame.NodeCount != Nodes.Count || !_sequencer.TryAccept(frame))
        {
            return false;
        }

        _interpolator.Push(frame, nowSeconds);
        _grid.Rebuild(frame.Positions.Span);
        _animateUntilSeconds = nowSeconds + 0.25;
        return true;
    }

    /// <summary>
    /// Accepts the LOD snapshot for a graph version. A snapshot older than the newest one applied is dropped. A newer version
    /// clears the tile nodes, because they belong to the old graph. Returns true when the snapshot was applied.
    /// </summary>
    public bool ApplyLodSnapshot(GraphLodFrame lod)
    {
        ArgumentNullException.ThrowIfNull(lod);
        bool newerVersion;
        lock (_gate)
        {
            if (lod.GraphVersion < _graphVersion)
            {
                return false;
            }

            newerVersion = lod.GraphVersion > _graphVersion;
            _graphVersion = lod.GraphVersion;
            _clustersOnly = lod.ClustersOnly;
            if (newerVersion)
            {
                _tileNodes = [];
                _lastTileSeq = -1;
            }
        }

        Notify(nameof(GraphVersion), nameof(ClustersOnly), nameof(TileNodes), nameof(HasData));
        return true;
    }

    /// <summary>
    /// Asks for the nodes inside the visible box. Only one request is in flight: a box asked for while a fetch runs replaces
    /// any box still waiting, so the fetch loop sends the newest box once the current response returns. Returns when the box
    /// is queued or, if this call starts the loop, when the loop has finished. Does nothing without a tile source, before a
    /// LOD snapshot, or while the graph is above the node cap.
    /// </summary>
    public Task SetViewportAsync(ConstellationBox box, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_tiles is null || _graphVersion < 0 || _clustersOnly)
            {
                return Task.CompletedTask;
            }

            _queued = new ViewportRequest(
                viewId: Kind,
                graphVersion: _graphVersion,
                tier: Bazaar.Contracts.Graph.ZoomTier.Near,
                minX: box.MinX,
                minY: box.MinY,
                minZ: 0,
                maxX: box.MaxX,
                maxY: box.MaxY,
                maxZ: 0,
                maxNodes: NodeCap);

            if (_fetching)
            {
                return Task.CompletedTask;
            }

            _fetching = true;
        }

        return FetchLatestAsync(cancellationToken);
    }

    /// <summary>The visible box for a viewport of this size, in graph coordinates.</summary>
    public ConstellationBox VisibleBox(int widthPixels, int heightPixels)
    {
        var (x0, y0) = Camera.ScreenToWorld(0, 0, widthPixels, heightPixels);
        var (x1, y1) = Camera.ScreenToWorld(widthPixels, heightPixels, widthPixels, heightPixels);
        return new ConstellationBox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>Runs one navigation command and returns the focus changes it produced.</summary>
    public IReadOnlyList<ConstellationFocusRequest> Command(ConstellationCommand command)
    {
        if (command.Kind == ConstellationCommandKind.Focus && (command.NodeId is null || !_indexById.ContainsKey(command.NodeId)))
        {
            return [];
        }

        var requests = Navigation.Apply(command);
        if (requests.Count > 0)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusedNodeId)));
        }

        return requests;
    }

    public bool IsFocused(string nodeId) => string.Equals(FocusedNodeId, nodeId, StringComparison.Ordinal);

    /// <summary>Index of a node in <see cref="Nodes"/>, or -1 when unknown.</summary>
    public int IndexOf(string nodeId) => _indexById.TryGetValue(nodeId, out int index) ? index : -1;

    private async Task FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ViewportRequest request;
                lock (_gate)
                {
                    if (_queued is null)
                    {
                        _fetching = false;
                        return;
                    }

                    request = _queued;
                    _queued = null;
                }

                TileFrame? frame = await _tiles!.GetFrameAsync(request, cancellationToken);
                AcceptTile(frame, request);
            }
        }
        catch
        {
            lock (_gate)
            {
                _fetching = false;
            }

            throw;
        }
    }

    /// <summary>Applies a tile response unless it is stale, from another view, or over the node cap.</summary>
    private void AcceptTile(TileFrame? frame, ViewportRequest request)
    {
        bool changed;
        lock (_gate)
        {
            if (request.GraphVersion != _graphVersion)
            {
                return;
            }

            if (frame is null)
            {
                changed = _tileNodes.Length > 0;
                _tileNodes = [];
            }
            else if (frame.GraphVersion != _graphVersion || frame.ViewId != Kind ||
                     frame.LayoutSeq < _lastTileSeq || frame.NodeIds.Count > NodeCap || frame.NodeIds.Count > request.MaxNodes)
            {
                return;
            }
            else
            {
                var span = frame.Xyz.Span;
                var nodes = new TileNode[frame.NodeIds.Count];
                for (int i = 0; i < nodes.Length; i++)
                {
                    nodes[i] = new TileNode(frame.NodeIds[i], span[i * 3], span[(i * 3) + 1]);
                }

                _tileNodes = nodes;
                _lastTileSeq = frame.LayoutSeq;
                changed = true;
            }
        }

        if (changed)
        {
            Notify(nameof(TileNodes), nameof(HasData));
        }
    }

    private void Notify(params string[] properties)
    {
        foreach (var property in properties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private static bool TryReadNode(JsonElement node, out ConstellationNode parsed)
    {
        parsed = null!;
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string idText = id.GetString()!;
        if (idText.Length == 0)
        {
            return false;
        }

        string label = node.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : idText;
        string kind = node.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "node";
        parsed = new ConstellationNode(idText, label, kind);
        return true;
    }
}
