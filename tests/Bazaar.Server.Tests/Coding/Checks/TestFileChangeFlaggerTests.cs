using Bazaar.Server.Coding.Checks;

namespace Bazaar.Server.Tests.Coding.Checks;

/// <summary>Test-file flag (doc 17, Verification step 3): any changed test path needs a named reviewer.</summary>
public sealed class TestFileChangeFlaggerTests
{
    [Theory]
    [InlineData("tests/Bazaar.Server.Tests/ThingTests.cs")]
    [InlineData("src/Bazaar.Server/ThingTests.cs")]
    [InlineData("src/Bazaar.Server/ThingTest.cs")]
    [InlineData("src/Foo/tests/helper.cs")]
    [InlineData("tests/fixtures/data.json")]
    public void TestPathIsFlagged(string path)
    {
        var finding = Assert.Single(TestFileChangeFlagger.Flag([new ChangedFile(path, ChangeKind.Modified, "a", "b")]));

        Assert.Equal(FlagCodes.TestFileChanged, finding.Code);
        Assert.Equal(path, finding.Path);
    }

    [Theory]
    [InlineData("src/Bazaar.Server/Thing.cs")]
    [InlineData("src/Bazaar.Server/Contest.cs")]
    [InlineData("docs/tests-plan.md")]
    [InlineData("src/Bazaar.Server/testing/Helper.cs")]
    public void NonTestPathIsNotFlagged(string path)
    {
        Assert.Empty(TestFileChangeFlagger.Flag([new ChangedFile(path, ChangeKind.Modified, "a", "b")]));
    }

    [Fact]
    public void AddedTestFileIsStillFlagged()
    {
        // Adding a test is a change to the suite too; the flag is about who reviews it, not whether it is an edit.
        var finding = Assert.Single(TestFileChangeFlagger.Flag([new ChangedFile("tests/Bazaar.Server.Tests/NewTests.cs", ChangeKind.Added, null, "x")]));

        Assert.Equal("Added test file: tests/Bazaar.Server.Tests/NewTests.cs", finding.Detail);
    }

    [Fact]
    public void OnlyTestFilesAreReturned()
    {
        var findings = TestFileChangeFlagger.Flag(
        [
            new ChangedFile("src/Bazaar.Server/Thing.cs", ChangeKind.Modified, "a", "b"),
            new ChangedFile("tests/Bazaar.Server.Tests/ThingTests.cs", ChangeKind.Modified, "a", "b"),
            new ChangedFile("src/Bazaar.Server/Other.cs", ChangeKind.Deleted, "a", null),
        ]);

        Assert.Equal("tests/Bazaar.Server.Tests/ThingTests.cs", Assert.Single(findings).Path);
    }
}
