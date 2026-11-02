using Bazaar.Server.Souls.Bundles;

namespace Bazaar.Server.Tests.Souls.Bundles;

public sealed class BundlePathTests
{
    [Theory]
    [InlineData("charter.md")]
    [InlineData("skills/a/SKILL.md")]
    [InlineData("skills/a.b-c_d/SKILL.md")]
    [InlineData("a/b/c/d.txt")]
    public void Relative_forward_slash_paths_are_accepted(string path)
    {
        Assert.Equal(path, BundlePaths.Validate(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("C:relative")]
    [InlineData("https:/x")]
    [InlineData("../escape.md")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("..\\escape.md")]
    [InlineData("a\\b")]
    [InlineData("./a")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a\u0007b")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => BundlePaths.Validate(path));
    }

    [Fact]
    public void Over_long_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => BundlePaths.Validate(new string('a', 513)));
    }

    [Theory]
    [InlineData("/abs/skills/a/SKILL.md")]
    [InlineData("skills/../../escape/SKILL.md")]
    [InlineData("skills\\..\\escape.md")]
    public void The_exporter_refuses_unsafe_extra_file_paths(string path)
    {
        var request = BundleFixture.Request([BundleFixture.Skill(path, "body")]);

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(request));
    }
}
