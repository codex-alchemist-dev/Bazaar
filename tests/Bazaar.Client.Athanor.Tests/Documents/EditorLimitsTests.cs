namespace Bazaar.Client.Athanor.Tests.Documents;

public class EditorLimitsTests
{
    [Fact]
    public void The_default_open_limit_is_5_MiB()
    {
        Assert.Equal(5L * 1024 * 1024, EditorLimits.Default.MaxOpenBytes);
    }

    [Fact]
    public void The_open_limit_can_be_configured()
    {
        var limits = EditorLimits.Default with { MaxOpenBytes = 1024 };

        Assert.Equal(1024, limits.MaxOpenBytes);
        Assert.Equal(5L * 1024 * 1024, EditorLimits.Default.MaxOpenBytes);
    }

    [Theory]
    [InlineData("a\0b", true)]
    [InlineData("\0", true)]
    [InlineData("plain text", false)]
    [InlineData("", false)]
    [InlineData("tab\tand\nnewline", false)]
    public void Content_with_a_NUL_is_binary(string content, bool expected)
    {
        Assert.Equal(expected, EditorLimits.IsBinary(content.AsSpan()));
    }
}
