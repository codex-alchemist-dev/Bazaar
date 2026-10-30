using Bazaar.Client.Linux;

namespace Bazaar.Client.Linux.Tests;

public class LinuxPathsTests
{
    private static Func<string, string?> Env(string? xdgState) =>
        key => key == "XDG_STATE_HOME" ? xdgState : null;

    [Fact]
    public void Falls_back_to_the_home_state_directory_when_xdg_is_unset()
    {
        var paths = LinuxPaths.FromEnvironment(Env(null), "/home/dom");

        Assert.Equal(Path.Combine("/home/dom", ".local", "state", "bazaar"), paths.StateDirectory);
        Assert.Equal(Path.Combine("/home/dom", ".local", "state", "bazaar", "cursors.json"), paths.CursorFile);
    }

    [Fact]
    public void Uses_xdg_state_home_when_it_is_absolute()
    {
        var paths = LinuxPaths.FromEnvironment(Env("/srv/state"), "/home/dom");

        Assert.Equal(Path.Combine("/srv/state", "bazaar"), paths.StateDirectory);
    }

    [Fact]
    public void Ignores_a_relative_xdg_state_home()
    {
        var paths = LinuxPaths.FromEnvironment(Env("relative/state"), "/home/dom");

        Assert.Equal(Path.Combine("/home/dom", ".local", "state", "bazaar"), paths.StateDirectory);
    }

    [Fact]
    public void Rejects_an_empty_home_directory()
    {
        Assert.Throws<ArgumentException>(() => LinuxPaths.FromEnvironment(Env(null), " "));
    }
}
