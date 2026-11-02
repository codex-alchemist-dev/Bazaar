using Bazaar.Client.LocalInference.BlobCache;

namespace Bazaar.Client.LocalInference.Tests.BlobCache;

public class BlobCacheKeyTests
{
    [Fact]
    public void Equal_keys_have_equal_text_and_directory_names()
    {
        var first = new BlobCacheKey("model-a", "OpenVINO", "32.0.100.5540", "2026.2.0");
        var second = new BlobCacheKey("model-a", "OpenVINO", "32.0.100.5540", "2026.2.0");

        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(first.DirectoryName, second.DirectoryName);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Directory_name_is_a_lowercase_sha256_hex_string()
    {
        var key = new BlobCacheKey("model-a", "CPU", null, "1.24.1");

        Assert.Matches("^[0-9a-f]{64}$", key.DirectoryName);
    }

    [Fact]
    public void Null_driver_and_a_driver_spelled_as_dash_are_different_keys()
    {
        var cpu = new BlobCacheKey("model-a", "CPU", null, "1.24.1");
        var dash = new BlobCacheKey("model-a", "CPU", "-", "1.24.1");

        Assert.NotEqual(cpu.ToString(), dash.ToString());
        Assert.NotEqual(cpu.DirectoryName, dash.DirectoryName);
    }

    [Fact]
    public void Field_boundaries_do_not_let_different_keys_share_a_form()
    {
        var splitOne = new BlobCacheKey("a|b", "c", null, "d");
        var splitTwo = new BlobCacheKey("a", "b|c", null, "d");

        Assert.NotEqual(splitOne.ToString(), splitTwo.ToString());
        Assert.NotEqual(splitOne.DirectoryName, splitTwo.DirectoryName);
    }

    [Theory]
    [InlineData("", "CPU", null, "1.24.1")]
    [InlineData("model-a", " ", null, "1.24.1")]
    [InlineData("model-a", "CPU", "", "1.24.1")]
    [InlineData("model-a", "CPU", null, "")]
    public void Rejects_blank_required_fields(string model, string provider, string? driver, string runtime)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BlobCacheKey(model, provider, driver, runtime));
    }
}
