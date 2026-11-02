using Bazaar.Constellation.Rendering;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Vulkan backend for the Constellation view (doc 19, WP7).
/// This slice probes the loader and devices and keeps the shared frame state (ordering, blending, scene checks).
/// The swapchain, pipelines and drawing are not implemented yet: they need a GPU run on the owner's Windows PC.
/// Until then <see cref="Attach"/> reports why it cannot draw, and it never claims a surface it did not bind.
/// </summary>
public sealed class VulkanConstellationRenderer : IConstellationRenderer
{
    private const string _swapchainPending = "Vulkan swapchain and pipelines are not implemented yet (GPU spike pending on the owner's PC).";

    private readonly Lazy<VulkanProbeResult> _probe;
    private readonly FrameSequencer _sequencer = new();
    private readonly FrameInterpolator _interpolator;
    private ConstellationScene? _scene;
    private int _widthPixels;
    private int _heightPixels;
    private bool _disposed;

    public VulkanConstellationRenderer(Func<VulkanProbeResult>? probe = null, double frameBlendSeconds = 0.25)
    {
        _probe = new Lazy<VulkanProbeResult>(probe ?? VulkanProbe.Run);
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
        VulkanProbeResult probe = _probe.Value;
        if (!probe.Available)
        {
            return new RenderAttachResult(false, probe.Reason);
        }

        return new RenderAttachResult(false, _swapchainPending);
    }

    public void SetScene(ConstellationScene scene)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        _sequencer.Reset();
        _interpolator.Reset();
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
    }

    /// <summary>Nothing is drawn until a swapchain is bound by <see cref="Attach"/>.</summary>
    public void Draw(double nowSeconds)
    {
        ThrowIfDisposed();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
