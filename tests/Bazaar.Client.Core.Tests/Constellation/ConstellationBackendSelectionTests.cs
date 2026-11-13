using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Tests.Constellation;

public class ConstellationBackendSelectionTests
{
    [Fact]
    public void Chooses_vulkan_when_a_device_and_a_native_window_are_available()
    {
        ConstellationBackendChoice choice = ConstellationBackendSelection.Choose(
            vulkanDeviceAvailable: true, hostHasNativeWindowHandle: true, userPrefersSkia: false);

        Assert.Equal(ConstellationBackend.Vulkan, choice.Backend);
    }

    [Fact]
    public void Falls_back_to_skia_when_no_vulkan_device_is_found()
    {
        ConstellationBackendChoice choice = ConstellationBackendSelection.Choose(
            vulkanDeviceAvailable: false, hostHasNativeWindowHandle: true, userPrefersSkia: false);

        Assert.Equal(ConstellationBackend.Skia, choice.Backend);
        Assert.Contains("No Vulkan device", choice.Reason);
    }

    [Fact]
    public void Falls_back_to_skia_when_the_host_has_no_native_window()
    {
        ConstellationBackendChoice choice = ConstellationBackendSelection.Choose(
            vulkanDeviceAvailable: true, hostHasNativeWindowHandle: false, userPrefersSkia: false);

        Assert.Equal(ConstellationBackend.Skia, choice.Backend);
        Assert.Contains("no native window", choice.Reason);
    }

    [Fact]
    public void Respects_the_user_preference_for_skia_even_when_vulkan_is_available()
    {
        ConstellationBackendChoice choice = ConstellationBackendSelection.Choose(
            vulkanDeviceAvailable: true, hostHasNativeWindowHandle: true, userPrefersSkia: true);

        Assert.Equal(ConstellationBackend.Skia, choice.Backend);
        Assert.Contains("chose the Skia path", choice.Reason);
    }
}
