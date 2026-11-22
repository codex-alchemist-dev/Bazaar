using System.Reflection;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// The reversed-Z depth attachment is opt-in on the offscreen renderer and the swapchain. The option checks run anywhere;
/// the GPU tests skip where the loader finds no usable device (or no headless surface extension).
/// </summary>
public class ReversedZTargetTests
{
    private const string _headlessExtension = "VK_EXT_headless_surface";

    [Fact]
    public void Offscreen_create_defaults_reversed_z_to_off()
    {
        ParameterInfo parameter = typeof(VulkanOffscreenRenderer).GetMethod(nameof(VulkanOffscreenRenderer.Create))!
            .GetParameters().Single(p => p.Name == "reversedZ");
        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(false, parameter.DefaultValue);
    }

    [Fact]
    public void Swapchain_try_create_defaults_reversed_z_to_off()
    {
        ParameterInfo parameter = typeof(VulkanSwapchain).GetMethod(nameof(VulkanSwapchain.TryCreate))!
            .GetParameters().Single(p => p.Name == "reversedZ");
        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(false, parameter.DefaultValue);
    }

    [Fact]
    public void Offscreen_without_reversed_z_has_no_depth_view()
    {
        using VulkanOffscreenRenderer renderer = CreateOffscreenOrSkip(reversedZ: false);
        Assert.False(renderer.ReversedZEnabled);
        Assert.Null(renderer.DepthImage);
        Assert.Null(renderer.DepthView);
    }

    [Fact]
    public void Offscreen_with_reversed_z_exposes_a_depth_view_the_hiz_pass_accepts()
    {
        using VulkanOffscreenRenderer renderer = CreateOffscreenOrSkip(reversedZ: true);
        Assert.True(renderer.ReversedZEnabled);
        Assert.NotNull(renderer.DepthImage);
        Assert.NotNull(renderer.DepthView);

        using var pass = new HiZPyramidPass(
            renderer.InstanceApi, renderer.DeviceApi, renderer.PhysicalDevice,
            renderer.DepthImage!.Value, renderer.DepthView!.Value, 64, 64);
        Assert.True(pass.MipCount > 0);
        Assert.DoesNotContain(VulkanOffscreenRenderer.DrainValidationMessages(), m => m.Severity.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Offscreen_renders_the_same_pixels_with_and_without_reversed_z()
    {
        byte[] plain = RenderOne(reversedZ: false);
        byte[] reversed = RenderOne(reversedZ: true);
        Assert.Equal(plain, reversed);
    }

    [Fact]
    public void Swapchain_with_reversed_z_keeps_a_depth_view_across_resize()
    {
        VulkanProbeResult probe = VulkanProbe.Run();
        if (!probe.Available)
        {
            Assert.Skip(probe.Reason);
        }

        if (!VulkanProbe.InstanceExtensionNames().Contains(_headlessExtension))
        {
            Assert.Skip($"{_headlessExtension} is not offered by this Vulkan loader.");
        }

        (VulkanSwapchain? swapchain, string reason) =
            VulkanSwapchain.TryCreate(new RenderSurface(RenderSurfaceKind.Headless, 0, 0, 320, 200), reversedZ: true);
        if (swapchain is null)
        {
            Assert.Skip(reason);
        }

        using (swapchain)
        {
            Assert.True(swapchain.ReversedZEnabled);
            Assert.NotNull(swapchain.DepthView);
            Assert.Equal(string.Empty, swapchain.Resize(400, 300));
            Assert.NotNull(swapchain.DepthView);
            Assert.True(swapchain.Present((0.05f, 0.06f, 0.08f, 1f), ReadOnlySpan<byte>.Empty, 0, NodeView.Identity));
        }
    }

    private static VulkanOffscreenRenderer CreateOffscreenOrSkip(bool reversedZ)
    {
        try
        {
            return VulkanOffscreenRenderer.Create(64, 64, enableValidation: true, reversedZ: reversedZ);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    private static byte[] RenderOne(bool reversedZ)
    {
        using VulkanOffscreenRenderer renderer = CreateOffscreenOrSkip(reversedZ);
        var styles = new NodeStyleTable(1, NodeStyle.Create(1f, 0f, 0f, 1f, 0.3f));
        NodeStylePalette palette = NodeStylePalette.Build(styles);
        renderer.SetStyles(NodeStylePacking.Pack(palette));
        var instances = new NodeInstanceBuffer();
        instances.Fill([0f, 0f], 2, palette);
        renderer.Render((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
        return renderer.ReadPixels().ToArray();
    }
}
