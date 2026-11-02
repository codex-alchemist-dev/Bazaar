namespace Bazaar.Constellation.Vulkan.Tests;

public class VulkanProbeTests
{
    [Fact]
    public void Probe_reports_a_consistent_result_on_any_machine()
    {
        VulkanProbeResult result = VulkanProbe.Run();

        if (result.Available)
        {
            Assert.NotEmpty(result.Devices);
            Assert.Empty(result.Reason);
        }
        else
        {
            Assert.NotEmpty(result.Reason);
        }
    }

    [Theory]
    [InlineData(1, 0, 0, 0x00400000u)]
    [InlineData(1, 4, 0, 0x00404000u)]
    public void Packs_versions_like_VK_MAKE_API_VERSION(uint major, uint minor, uint patch, uint expected) =>
        Assert.Equal(expected, VulkanProbe.MakeVersion(major, minor, patch));

    [Fact]
    public void Formats_versions_as_major_minor_patch() =>
        Assert.Equal("1.4.0", VulkanProbe.VersionString(VulkanProbe.MakeVersion(1, 4, 0)));

    [Fact]
    public void Target_version_is_at_least_1_4()
    {
        Assert.True(VulkanProbe.TargetApiVersion >= VulkanProbe.MakeVersion(1, 4, 0));
    }
}
