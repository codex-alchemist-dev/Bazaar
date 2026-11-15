using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Vulkan backend for the Constellation view (doc 19, WP7).
/// Attach binds a <see cref="VulkanSwapchain"/> to the surface, and Draw clears and presents each frame.
/// Node drawing is not in this slice yet: frames are accepted and ordered, and the swapchain shows the background.
/// The backend never claims a surface it did not bind, and reports why when it cannot draw.
/// </summary>
public sealed class VulkanConstellationRenderer : IConstellationRenderer
{
    /// <summary>Background colour cleared each frame until node drawing lands (RGBA, linear).</summary>
    private static readonly (float R, float G, float B, float A) _background = (0.05f, 0.06f, 0.08f, 1f);

    private readonly Lazy<VulkanProbeResult> _probe;
    private readonly Func<RenderSurface, (VulkanSwapchain? Swapchain, string Reason)> _createSwapchain;
    private readonly FrameSequencer _sequencer = new();
    private readonly FrameInterpolator _interpolator;
    private VulkanSwapchain? _swapchain;
    private ConstellationScene? _scene;
    private int _widthPixels;
    private int _heightPixels;
    private bool _disposed;

    public VulkanConstellationRenderer(
        Func<VulkanProbeResult>? probe = null,
        double frameBlendSeconds = 0.25,
        Func<RenderSurface, (VulkanSwapchain? Swapchain, string Reason)>? createSwapchain = null)
    {
        _probe = new Lazy<VulkanProbeResult>(probe ?? VulkanProbe.Run);
        _createSwapchain = createSwapchain ?? VulkanSwapchain.TryCreate;
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
        _widthPixels = surface.WidthPixels;
        _heightPixels = surface.HeightPixels;
        return new RenderAttachResult(true, string.Empty);
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
        if (_swapchain is not null)
        {
            _ = _swapchain.Resize(widthPixels, heightPixels);
        }
    }

    /// <summary>Clears and presents the background. Does nothing until a surface is attached.</summary>
    public void Draw(double nowSeconds)
    {
        ThrowIfDisposed();
        if (_swapchain is null)
        {
            return;
        }

        _ = _swapchain.ClearAndPresent(_background);
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
