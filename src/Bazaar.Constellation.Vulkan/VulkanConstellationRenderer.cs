using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Vulkan backend for the Constellation view (doc 19, WP7).
/// Attach binds a <see cref="VulkanSwapchain"/> to the surface, and Draw clears and presents each frame.
/// Draw clears the background and draws the latest interpolated frame as instanced nodes. Each node takes its colour,
/// radius and ring width from its style: SetScene gives every node the default style,
/// and SetNodeStyles replaces that with explicit per-node styles (the adapter colours nodes by cluster this way).
/// The backend never claims a surface it did not bind, and reports why when it cannot draw.
/// </summary>
public sealed class VulkanConstellationRenderer : IConstellationRenderer
{
    /// <summary>Background colour cleared each frame until node drawing lands (RGBA, linear).</summary>
    private static readonly (float R, float G, float B, float A) _background = ConstellationNodeStyles.DefaultTheme.Background;

    /// <summary>Style used for every node until per-node styles arrive from the adapter (RGBA, radius in world units).</summary>
    private static readonly NodeStyle _nodeStyle = NodeStyle.Create(0.80f, 0.86f, 0.95f, 1f, 0.012f);

    private readonly Lazy<VulkanProbeResult> _probe;
    private readonly Func<RenderSurface, (VulkanSwapchain? Swapchain, string Reason)> _createSwapchain;
    private readonly FrameSequencer _sequencer = new();
    private readonly FrameInterpolator _interpolator;
    private readonly NodeInstanceBuffer _instances = new();
    private NodeStyleTable? _styles;
    private NodeStylePalette? _palette;
    private int _dimensions;
    private float[] _positions = [];
    private VulkanSwapchain? _swapchain;
    private RenderScene? _scene;
    private int _widthPixels;
    private int _heightPixels;
    private bool _disposed;

    public VulkanConstellationRenderer(
        Func<VulkanProbeResult>? probe = null,
        double frameBlendSeconds = 0.25,
        Func<RenderSurface, (VulkanSwapchain? Swapchain, string Reason)>? createSwapchain = null)
    {
        _probe = new Lazy<VulkanProbeResult>(probe ?? VulkanProbe.Run);
        _createSwapchain = createSwapchain ?? (surface => VulkanSwapchain.TryCreate(surface));
        _interpolator = new FrameInterpolator(frameBlendSeconds);
    }

    public RendererCapabilities Capabilities
    {
        get
        {
            VulkanProbeResult probe = _probe.Value;
            IReadOnlyList<string> devices = probe.Devices.Select(device => device.Name).ToList();
            IReadOnlyList<string> optional = probe.Devices.Any(device => device.MeshShaderExtension)
                ? ["mesh-shader"]
                : [];
            return new RendererCapabilities("vulkan", probe.InstanceApiVersion, devices, optional);
        }
    }

    public RenderAttachResult Attach(RenderSurface surface)
    {
        ThrowIfDisposed();
        if (_swapchain is not null)
        {
            return new RenderAttachResult(false, "A surface is already attached. Detach it before attaching another.");
        }

        VulkanProbeResult probe = _probe.Value;
        if (!probe.Available)
        {
            return new RenderAttachResult(false, probe.Reason);
        }

        (VulkanSwapchain? swapchain, string reason) = _createSwapchain(surface);
        if (swapchain is null)
        {
            return new RenderAttachResult(false, reason);
        }

        _swapchain = swapchain;
        if (_palette is not null)
        {
            swapchain.SetStyles(NodeStylePacking.Pack(_palette));
        }

        _widthPixels = surface.WidthPixels;
        _heightPixels = surface.HeightPixels;
        return new RenderAttachResult(true, string.Empty);
    }

    public void SetScene(RenderScene scene)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        _styles = new NodeStyleTable(scene.NodeCount, _nodeStyle);
        RebuildPalette();
        _instances.Clear();
        _dimensions = 0;
        _sequencer.Reset();
        _interpolator.Reset();
    }

    /// <inheritdoc/>
    public void SetNodeStyles(NodeStyleTable styles)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(styles);
        if (_scene is null)
        {
            throw new InvalidOperationException("Set the scene before its node styles.");
        }

        if (styles.NodeCount != _scene.NodeCount)
        {
            throw new ArgumentException("The style table must have one entry per node of the scene.", nameof(styles));
        }

        _styles = styles;
        RebuildPalette();
    }

    /// <summary>Rebuilds the palette from the current table and sends its packed styles to the swapchain, when one is attached.</summary>
    private void RebuildPalette()
    {
        if (_styles is null)
        {
            _palette = null;
            return;
        }

        _palette = NodeStylePalette.Build(_styles);
        _swapchain?.SetStyles(NodeStylePacking.Pack(_palette));
    }

    public bool SubmitFrame(RenderFrame frame, double nowSeconds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        if (_scene is null || frame.NodeCount != _scene.NodeCount)
        {
            return false;
        }

        if (!_sequencer.TryAccept(frame))
        {
            return false;
        }

        _dimensions = frame.Dimensions;
        _interpolator.Push(frame, nowSeconds);
        return true;
    }

    public void Resize(int widthPixels, int heightPixels)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        _widthPixels = widthPixels;
        _heightPixels = heightPixels;
        if (_swapchain is not null)
        {
            _ = _swapchain.Resize(widthPixels, heightPixels);
        }
    }

    /// <summary>
    /// Clears the background, draws the interpolated node instances, and presents.
    /// Does nothing until a surface is attached. Before the first accepted frame only the background is shown.
    /// </summary>
    public void Draw(double nowSeconds)
    {
        ThrowIfDisposed();
        if (_swapchain is null)
        {
            return;
        }

        ReadOnlySpan<float> positions = SamplePositions(nowSeconds);
        if (positions.IsEmpty || _palette is null)
        {
            _ = _swapchain.Present(_background, ReadOnlySpan<byte>.Empty, 0, NodeView.Identity);
            return;
        }

        _instances.Fill(positions, _dimensions, _palette);
        NodeView view = FitView(positions, _dimensions);
        _ = _swapchain.Present(_background, _instances.Bytes, _instances.InstanceCount, view);
    }

    private ReadOnlySpan<float> SamplePositions(double nowSeconds)
    {
        if (!_interpolator.HasFrame || _dimensions == 0)
        {
            return ReadOnlySpan<float>.Empty;
        }

        int length = _interpolator.NodeCount * _dimensions;
        if (_positions.Length < length)
        {
            _positions = new float[length];
        }

        Span<float> destination = _positions.AsSpan(0, length);
        return _interpolator.Sample(nowSeconds, destination) ? destination : ReadOnlySpan<float>.Empty;
    }

    /// <summary>
    /// Scales the scene so the furthest node sits at 90% of the half-window, keeping world units square on screen.
    /// Positions are read as x, y (z is ignored for 3D input).
    /// </summary>
    private NodeView FitView(ReadOnlySpan<float> positions, int dimensions)
    {
        float extent = 1e-6f;
        for (int index = 0; index + 1 < positions.Length; index += dimensions)
        {
            extent = MathF.Max(extent, MathF.Max(MathF.Abs(positions[index]), MathF.Abs(positions[index + 1])));
        }

        float scaleY = 0.9f / extent;
        float aspect = _heightPixels > 0 && _widthPixels > 0 ? (float)_heightPixels / _widthPixels : 1f;
        return new NodeView(0f, 0f, scaleY * aspect, scaleY);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _swapchain?.Dispose();
        _swapchain = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
