using Bazaar.Server.Memory.Access;

namespace Bazaar.Server.Tests.Memory.Access;

public class VaultPathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../secrets.md")]
    [InlineData("agents/../bob/memory/x.md")]
    [InlineData("agents/alice/../bob/memory/x.md")]
    [InlineData("./notes.md")]
    [InlineData("agents/./alice/x.md")]
    [InlineData("agents//alice/x.md")]
    [InlineData("agents/alice/")]
    [InlineData("agents\\alice\\x.md")]
    [InlineData("agents/alice/x\0.md")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("agents/alice/x.md:hidden")]
    public void Parse_rejects_traversal_absolute_and_malformed_paths(string input)
    {
        Assert.Throws<ArgumentException>(() => VaultPath.Parse(input));
        Assert.False(VaultPath.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_rejects_null()
    {
        Assert.False(VaultPath.TryParse(null, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Segments_and_string_form_follow_the_normalized_path()
    {
        var path = VaultPath.Parse("agents/alice/memory/lesson.md");

        Assert.Equal(["agents", "alice", "memory", "lesson.md"], path.Segments);
        Assert.Equal("agents/alice/memory/lesson.md", path.ToString());
        Assert.Equal("agents/alice/memory/lesson.md", path.Value);
    }

    [Fact]
    public void Equal_strings_give_equal_paths()
    {
        Assert.Equal(VaultPath.Parse("projects/alpha/Index.md"), VaultPath.Parse("projects/alpha/Index.md"));
    }
}
