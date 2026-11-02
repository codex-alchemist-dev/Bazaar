using Bazaar.Server.Coding.Workspace;

namespace Bazaar.Server.Tests.Coding.Workspace;

public class GuestPathTests
{
    private const string _root = "/work/agent-1";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("src/../../secret")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\windows")]
    public void Empty_absolute_or_escaping_paths_are_refused(string relative)
    {
        Assert.Throws<ArgumentException>(() => GuestPath.Resolve(_root, relative));
    }

    [Fact]
    public void Nested_relative_paths_resolve_under_the_root()
    {
        Assert.Equal("/work/agent-1/src/app/Program.cs", GuestPath.Resolve(_root, "src/app/Program.cs"));
        Assert.Equal("/work/agent-1/src/app", GuestPath.Resolve(_root + "/", "./src//app"));
        Assert.Equal("/work/agent-1/src/app", GuestPath.Resolve(_root, "src\\app"));
    }

    [Fact]
    public void Dot_resolves_to_the_root()
    {
        Assert.Equal(_root, GuestPath.Resolve(_root, "."));
    }

    [Fact]
    public void Dots_inside_a_name_are_not_parent_segments()
    {
        Assert.Equal("/work/agent-1/v1..2/file..txt", GuestPath.Resolve(_root, "v1..2/file..txt"));
    }
}
