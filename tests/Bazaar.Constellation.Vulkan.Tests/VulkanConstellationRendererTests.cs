using Bazaar.Constellation.Rendering;

namespace Bazaar.Constellation.Vulkan.Tests;

public class VulkanConstellationRendererTests
{
    private static readonly VulkanProbeResult _available = new(
        true,
        string.Empty,
        "1.4.0",
        [new VulkanDeviceInfo("Test GPU", "DiscreteGpu", "1.4.0", MeshShaderExtension: true)]);

    private static readonly VulkanProbeResult _missing = new(false, "Vulkan loader is not usable on this machine.", string.Empty, []);

    private static RenderSurface Surface => new(RenderSurfaceKind.Win32, 0, 0, 800, 600);

    [Fact]
    public void Capabilities_list_devices_and_optional_mesh_shading_from_the_probe()
    {
        using var renderer = new VulkanConstellationRenderer(() => _available);
        RendererCapabilities capabilities = renderer.Capabilities;
        Assert.Equal("vulkan", capabilities.Backend);
        Assert.Equal(["Test GPU"], capabilities.Devices);
        Assert.Equal(["mesh-shader"], capabilities.OptionalFeatures);
    }

    [Fact]
    public void Attach_explains_why_it_cannot_draw_when_no_device_is_present()
    {
        using var renderer = new VulkanConstellationRenderer(() => _missing);
        RenderAttachResult result = renderer.Attach(Surface);
        Assert.False(result.Attached);
        Assert.Equal(_missing.Reason, result.Reason);
    }

    [Fact]
    public void Attach_does_not_claim_a_surface_before_the_swapchain_exists()
    {
        using var renderer = new VulkanConstellationRenderer(() => _available);
        RenderAttachResult result = renderer.Attach(Surface);
        Assert.False(result.Attached);
        Assert.Contains("not implemented", result.Reason);
    }

    [Fact]
    public void Frames_are_rejected_without_a_scene_or_with_the_wrong_node_count()
    {
        using var renderer = new VulkanConstellationRenderer(() => _available);
        var frame = new RenderFrame(1, 1, 2, new float[] { 0f, 0f, 1f, 1f });
        Assert.False(renderer.SubmitFrame(frame, 0));

        renderer.SetScene(new ConstellationScene(3, default, default));
        Assert.False(renderer.SubmitFrame(frame, 0));
    }

    [Fact]
    public void Accepted_frames_must_be_newer_than_the_last_one()
    {
        using var renderer = new VulkanConstellationRenderer(() => _available);
        renderer.SetScene(new ConstellationScene(2, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 2, 2, new float[] { 0f, 0f, 1f, 1f }), 0));
        Assert.False(renderer.SubmitFrame(new RenderFrame(1, 1, 2, new float[] { 0f, 0f, 1f, 1f }), 0.1));
    }

    [Fact]
    public void SetScene_resets_the_frame_order()
    {
        using var renderer = new VulkanConstellationRenderer(() => _available);
        renderer.SetScene(new ConstellationScene(1, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(5, 9, 2, new float[] { 0f, 0f }), 0));
        renderer.SetScene(new ConstellationScene(1, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 1, 2, new float[] { 0f, 0f }), 0));
    }

    [Fact]
    public void Disposed_renderer_rejects_calls()
    {
        var renderer = new VulkanConstellationRenderer(() => _available);
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.Resize(10, 10));
    }
}
