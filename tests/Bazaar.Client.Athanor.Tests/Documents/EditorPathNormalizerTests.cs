namespace Bazaar.Client.Athanor.Tests.Documents;

public class EditorPathNormalizerTests
{
    [Theory]
    [InlineData("src/a.cs", "src/a.cs")]
    [InlineData("src\\a.cs", "src/a.cs")]
    [InlineData("/a//b/", "a/b")]
    [InlineData("a///b", "a/b")]
    [InlineData("a/./b", "a/b")]
    [InlineData("./a", "a")]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("a\\b\\..\\c", "a/c")]
    [InlineData("a/../a/b", "a/b")]
    [InlineData("name with spaces.txt", "name with spaces.txt")]
    [InlineData("...", "...")]
    public void Safe_paths_normalise(string raw, string expected)
    {
        Assert.True(EditorPathNormalizer.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("..\\x")]
    [InlineData("..\\..\\x")]
    [InlineData("/../x")]
    [InlineData("..")]
    [InlineData("a/../..")]
    [InlineData("a/..")]
    public void Traversal_that_leaves_the_root_is_rejected(string raw)
    {
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData("%2e%2e/x", "%2e%2e/x")]
    [InlineData("%2e%2e%2fx", "%2e%2e%2fx")]
    [InlineData("..%2fx", "..%2fx")]
    [InlineData("%2E%2E%5Cx", "%2E%2E%5Cx")]
    public void Encoded_traversal_is_not_decoded_and_stays_a_literal_name(string raw, string expected)
    {
        Assert.True(EditorPathNormalizer.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("C:/x")]
    [InlineData("C:\\x")]
    [InlineData("c:x")]
    [InlineData("/C:/x")]
    [InlineData("D:\\Users\\a.txt")]
    public void Drive_letters_are_rejected(string raw)
    {
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share")]
    [InlineData("\\\\?\\C:\\x")]
    public void UNC_prefixes_are_rejected(string raw)
    {
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    [InlineData("\0")]
    public void Control_characters_including_NUL_are_rejected(string raw)
    {
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("./")]
    [InlineData(".")]
    [InlineData("\\")]
    public void Empty_and_root_only_paths_are_rejected(string raw)
    {
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.False(EditorPathNormalizer.TryNormalize(null!, out _));
    }

    [Fact]
    public void A_path_of_exactly_the_maximum_length_is_accepted()
    {
        var raw = new string('a', EditorPathNormalizer.MaxPathLength);
        Assert.True(EditorPathNormalizer.TryNormalize(raw, out var normalized));
        Assert.Equal(raw, normalized);
    }

    [Fact]
    public void A_path_one_character_over_the_maximum_is_rejected()
    {
        var raw = new string('a', EditorPathNormalizer.MaxPathLength + 1);
        Assert.False(EditorPathNormalizer.TryNormalize(raw, out _));
    }

    [Fact]
    public void A_deep_path_within_the_limit_is_accepted()
    {
        var raw = string.Join('/', Enumerable.Repeat("dir", 200)) + "/file.txt";
        Assert.True(raw.Length < EditorPathNormalizer.MaxPathLength);
        Assert.True(EditorPathNormalizer.TryNormalize(raw, out _));
    }
}
