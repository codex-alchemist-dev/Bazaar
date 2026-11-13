using Bazaar.Client.Linux.Secrets;

namespace Bazaar.Client.Linux.Tests;

public class LinuxPairingFactoryTests
{
    [Fact]
    public void Finds_secret_tool_in_a_later_path_entry()
    {
        var found = LinuxPairingFactory.IsSecretToolAvailable(
            "/usr/bin" + Path.PathSeparator + "/opt/tools",
            file => file == Path.Combine("/opt/tools", "secret-tool"));

        Assert.True(found);
    }

    [Fact]
    public void Reports_missing_when_no_path_entry_has_it()
    {
        Assert.False(LinuxPairingFactory.IsSecretToolAvailable("/usr/bin", _ => false));
    }

    [Fact]
    public void Reports_missing_for_an_empty_path()
    {
        Assert.False(LinuxPairingFactory.IsSecretToolAvailable(string.Empty, _ => true));
    }

    [Fact]
    public void Skips_empty_path_segments()
    {
        Assert.True(LinuxPairingFactory.IsSecretToolAvailable(
            Path.PathSeparator + "/usr/bin",
            file => file == Path.Combine("/usr/bin", "secret-tool")));
    }
}
