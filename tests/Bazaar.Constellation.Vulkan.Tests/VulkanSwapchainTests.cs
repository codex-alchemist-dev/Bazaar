using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// Runs the real loader on a headless surface. Skips where the machine has no Vulkan device (CI without a driver).
/// A software device such as lavapipe is enough, so these run on any Linux host with mesa-vulkan-drivers.
/// </summary>
public class VulkanSwapchainTests
{
    private static RenderSurface Headless(int width = 320, int height = 200) =>
        new(RenderSurfaceKind.Headless, 0, 0, width, height);

    private static VulkanProbeResult RequireDevice()
    {
        VulkanProbeResult probe = VulkanProbe.Run();
        if (!probe.Available)
        {
            Assert.Skip(probe.Reason);
        }

        return probe;
    }

    [Fact]
    public void Headless_surface_attaches_and_presents_frames()
    {
        RequireDevice();
        using var renderer = new VulkanConstellationRenderer();

        RenderAttachResult attach = renderer.Attach(Headless());
        Assert.True(attach.Attached, attach.Reason);

        renderer.SetScene(new ConstellationScene(2, default, default));
        Assert.True(renderer.SubmitFrame(new RenderFrame(1, 1, 2, new float[] { 0f, 0f, 1f, 1f }), 0));
        for (int tick = 0; tick < 5; tick++)
        {
            renderer.Draw(tick / 60.0);
        }
    }

    [Fact]
    public void Resize_rebuilds_the_swapchain_and_keeps_presenting()
    {
        RequireDevice();
        using var renderer = new VulkanConstellationRenderer();
        Assert.True(renderer.Attach(Headless(320, 200)).Attached);

        renderer.Resize(640, 480);
        renderer.Draw(0);
        renderer.Resize(128, 96);
        renderer.Draw(0.1);
    }

    [Fact]
    public void Second_attach_is_refused_while_a_surface_is_bound()
    {
        RequireDevice();
        using var renderer = new VulkanConstellationRenderer();
        Assert.True(renderer.Attach(Headless()).Attached);

        RenderAttachResult second = renderer.Attach(Headless());
        Assert.False(second.Attached);
        Assert.Contains("already attached", second.Reason);
    }

    [Fact]
    public void Disposing_the_renderer_releases_the_device_and_rejects_later_calls()
    {
        RequireDevice();
        var renderer = new VulkanConstellationRenderer();
        Assert.True(renderer.Attach(Headless()).Attached);
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => renderer.Draw(0));
    }
}
