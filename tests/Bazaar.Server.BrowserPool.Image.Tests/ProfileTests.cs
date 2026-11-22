namespace Bazaar.Server.BrowserPool.Image.Tests;

public class ProfileTests
{
    [Fact]
    public void Renders_the_standard_shape()
    {
        string text = BwrapAppArmorProfile.Render("/usr/bin/bwrap");

        Assert.Equal(
            "abi <abi/4.0>,\ninclude <tunables/global>\n\n"
            + "profile bazaar-pool-bwrap /usr/bin/bwrap flags=(unconfined) {\n"
            + "  userns,\n\n"
            + "  # Site-specific additions go in /etc/apparmor.d/local/bazaar-pool-bwrap.\n"
            + "  include if exists <local/bazaar-pool-bwrap>\n"
            + "}\n",
            text);
    }

    [Fact]
    public void The_path_is_the_only_interpolated_value()
    {
        Assert.Equal(
            BwrapAppArmorProfile.Render("/usr/bin/bwrap").Replace("/usr/bin/bwrap", "/opt/x/bwrap"),
            BwrapAppArmorProfile.Render("/opt/x/bwrap"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bwrap")]
    [InlineData("/usr/bin/../bwrap")]
    [InlineData("/usr/bin/bw rap")]
    [InlineData("/usr/bin/bwrap {")]
    [InlineData("/usr/bin/bwrap\n}\nprofile evil /bin/sh {")]
    [InlineData("/usr/bin/bw\"rap")]
    [InlineData("/usr/bin/bw*rap")]
    [InlineData("/usr/bin/bwrap/")]
    public void Unsafe_paths_throw(string path)
    {
        Assert.Throws<ArgumentException>(() => BwrapAppArmorProfile.Render(path));
    }

    [Fact]
    public void Install_names_are_stable()
    {
        Assert.Equal("bazaar-pool-bwrap", BwrapAppArmorProfile.InstallFileName);
        Assert.Equal("bazaar-pool-bwrap", BwrapAppArmorProfile.ProfileName);
        Assert.Equal("/etc/apparmor.d/bazaar-pool-bwrap", BwrapAppArmorProfile.InstallPath);
    }
}
