using System.ComponentModel;
using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Constellation.Rendering;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Constellation;

/// <summary>One node of the Constellation pane: its id, screen-reader label and kind.</summary>
public sealed record ConstellationNode(string Id, string Label, string Kind);

/// <summary>
/// View model for the Constellation 2D pane (doc 19). Model:
/// <c>{ "nodes": [ { "id", "label", "kind" } ], "edges": [ { "from", "to" } ] }</c>.
/// Layout frames come from the server's LOD feed through <see cref="ApplyFrame"/>. The model holds no Avalonia types.
/// </summary>
public sealed class ConstellationPaneViewModel : IPaneViewModel, INotifyPropertyChanged
{
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);
    private readonly FrameSequencer _sequencer = new();
    private readonly FrameInterpolator _interpolator = new();
    private readonly SpatialGrid _grid = new(cellSize: 32f);
    private double _animateUntilSeconds;

    public ConstellationPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = descriptor.Title;
        Navigation = new ConstellationNavigation(viewId: descriptor.Kind);
        Camera = new Camera2D();

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
