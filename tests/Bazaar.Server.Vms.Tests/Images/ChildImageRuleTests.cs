using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class ChildImageRuleTests
{
    [Fact]
    public void Missing_parent_refuses_the_child()
    {
        var result = ChildImageRule.CheckParent(ImageTestData.AbcSha256, null, parentExists: false);

        Assert.Equal(ParentCheckStatus.ParentMissing, result.Status);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void Missing_parent_is_refused_even_when_a_hash_is_given()
    {
        var result = ChildImageRule.CheckParent(ImageTestData.AbcSha256, ImageTestData.AbcSha256, parentExists: false);

        Assert.Equal(ParentCheckStatus.ParentMissing, result.Status);
    }

    [Fact]
    public void Different_parent_hash_refuses_the_child()
    {
        var result = ChildImageRule.CheckParent(ImageTestData.AbcSha256, ImageTestData.EmptySha256, parentExists: true);

        Assert.Equal(ParentCheckStatus.ParentHashMismatch, result.Status);
        Assert.Contains(ImageTestData.AbcSha256, result.Message);
        Assert.Contains(ImageTestData.EmptySha256, result.Message);
    }

    [Fact]
    public void Matching_parent_hash_is_ok()
    {
        var result = ChildImageRule.CheckParent(ImageTestData.AbcSha256, ImageTestData.AbcSha256, parentExists: true);

        Assert.Equal(ParentCheckStatus.Ok, result.Status);
        Assert.True(result.IsOk);
    }

    [Fact]
    public void Hash_comparison_ignores_hex_case()
    {
        var result = ChildImageRule.CheckParent(ImageTestData.AbcSha256, ImageTestData.AbcSha256.ToUpperInvariant(), parentExists: true);

        Assert.Equal(ParentCheckStatus.Ok, result.Status);
    }

    [Fact]
    public void Existing_parent_needs_its_actual_hash()
    {
        Assert.ThrowsAny<ArgumentException>(() => ChildImageRule.CheckParent(ImageTestData.AbcSha256, null, parentExists: true));
    }

    [Fact]
    public void Child_must_record_a_parent_hash()
    {
        Assert.ThrowsAny<ArgumentException>(() => ChildImageRule.CheckParent(" ", ImageTestData.AbcSha256, parentExists: true));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public void Parent_retires_only_when_no_live_child_remains(int liveChildren, bool expected)
    {
        Assert.Equal(expected, ChildImageRule.CanRetireParent(liveChildren));
    }

    [Fact]
    public void Negative_child_count_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChildImageRule.CanRetireParent(-1));
    }

    [Fact]
    public async Task Child_is_refused_once_its_parent_is_tampered_with()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "parent bytes");
        var fs = new PhysicalImageFileSystem();
        var hasher = new ImageHasher(fs);
        var sealedParent = await new GoldenImageSealer(fs, hasher).SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken);

        var before = ChildImageRule.CheckParent(sealedParent.Sha256, await hasher.HashFileAsync(path, TestContext.Current.CancellationToken), parentExists: true);
        Assert.True(before.IsOk);

        fs.SetReadOnly(path, isReadOnly: false);
        File.WriteAllText(path, "changed bytes");
        var after = ChildImageRule.CheckParent(sealedParent.Sha256, await hasher.HashFileAsync(path, TestContext.Current.CancellationToken), parentExists: true);

        Assert.Equal(ParentCheckStatus.ParentHashMismatch, after.Status);
    }
}
