using Bazaar.Server.Coding.Checks;

namespace Bazaar.Server.Tests.Coding.Checks;

/// <summary>Fake-success scan (doc 17, Verification step 4): each flag has a positive and a negative case.</summary>
public sealed class FakeSuccessScannerTests
{
    private const string _testPath = "tests/Bazaar.Server.Tests/ThingTests.cs";

    [Theory]
    [InlineData("[Skip(\"flaky\")]")]
    [InlineData("[Ignore(\"flaky\")]")]
    [InlineData("Ignore(\"flaky\");")]
    public void AddedSkipOrIgnoreIsFlagged(string line)
    {
        var findings = FakeSuccessScanner.Scan([Modified(_testPath, "public void A() { }\n", $"public void A() {{ }}\n{line}\n")]);

        var finding = Assert.Single(findings);
        Assert.Equal(FlagCodes.SkipAdded, finding.Code);
        Assert.Equal(_testPath, finding.Path);
        Assert.Equal(line, finding.Detail);
    }

    [Fact]
    public void PreExistingSkipIsNotFlagged()
    {
        const string old = "[Ignore(\"old\")]\npublic void A() { }\n";

        var findings = FakeSuccessScanner.Scan([Modified(_testPath, old, old + "public void B() { }\n")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void SecondCopyOfExistingSkipIsFlagged()
    {
        // Multiset comparison: the old text had one copy, so the second copy is new.
        const string line = "[Ignore(\"old\")]";

        var findings = FakeSuccessScanner.Scan([Modified(_testPath, $"{line}\n", $"{line}\n{line}\n")]);

        Assert.Equal(FlagCodes.SkipAdded, Assert.Single(findings).Code);
    }

    [Fact]
    public void SkipInSourceFileIsFlagged()
    {
        var findings = FakeSuccessScanner.Scan([Added("src/Bazaar.Server/Thing.cs", "[Ignore(\"x\")]\n")]);

        Assert.Equal(FlagCodes.SkipAdded, Assert.Single(findings).Code);
    }

    [Fact]
    public void SkipInUninspectedFileIsNotFlagged()
    {
        var findings = FakeSuccessScanner.Scan([Added("docs/notes.md", "[Skip] and Ignore(x)\n")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void SkipInFileUnderTestsFolderIsFlaggedEvenWhenNotSource()
    {
        var findings = FakeSuccessScanner.Scan([Added("tests/fixtures/cases.json", "[Skip\n")]);

        Assert.Equal(FlagCodes.SkipAdded, Assert.Single(findings).Code);
    }

    [Fact]
    public void AddedIfFalseIsFlagged()
    {
        var findings = FakeSuccessScanner.Scan([Modified("src/Bazaar.Server/Thing.cs", "void X() { }\n", "#if false\nvoid X() { }\n#endif\n")]);

        var finding = Assert.Single(findings);
        Assert.Equal(FlagCodes.IfFalseAdded, finding.Code);
        Assert.Equal("#if false", finding.Detail);
    }

    [Fact]
    public void PreExistingIfFalseIsNotFlagged()
    {
        const string old = "#if false\nvoid X() { }\n#endif\n";

        var findings = FakeSuccessScanner.Scan([Modified("src/Bazaar.Server/Thing.cs", old, old + "void Y() { }\n")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void RemovedTestAttributeIsFlagged()
    {
        const string old = "[Fact]\npublic void A() { }\n[Fact]\npublic void B() { }\n";
        const string head = "[Fact]\npublic void A() { }\n";

        var findings = FakeSuccessScanner.Scan([Modified(_testPath, old, head)]);

        var finding = Assert.Single(findings);
        Assert.Equal(FlagCodes.TestRemoved, finding.Code);
        Assert.Equal("Test attributes 2 -> 1.", finding.Detail);
    }

    [Fact]
    public void TestAttributeCountUnchangedIsNotFlagged()
    {
        // Same number of test attributes, one moved to a new method: not a removal.
        const string old = "[Fact]\npublic void A() { }\n[AvaloniaFact]\npublic void B() { }\n";
        const string head = "[AvaloniaFact]\npublic void B() { }\n[Fact]\npublic void A() { }\n";

        var findings = FakeSuccessScanner.Scan([Modified(_testPath, old, head)]);

        Assert.Empty(findings);
    }

    [Fact]
    public void FactoryAttributeIsNotCountedAsTest()
    {
        // "[Factory]" is not "[Fact]": removing it must not look like a test removal.
        var findings = FakeSuccessScanner.Scan([Modified("src/Bazaar.Server/Thing.cs", "[Factory]\n", "")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void DeletedTestFileIsFlaggedAsTestRemovedAndAssertionRemoved()
    {
        const string old = "[Test]\npublic void A() { Assert.True(true); }\n";

        var findings = FakeSuccessScanner.Scan([Deleted(_testPath, old)]);

        Assert.Equal([FlagCodes.TestRemoved, FlagCodes.AssertionRemoved], findings.Select(f => f.Code).ToArray());
    }

    [Fact]
    public void RemovedAssertionIsFlagged()
    {
        const string old = "Assert.True(a);\nAssert.Equal(1, b);\nAssert.NotNull(c);\n";
        const string head = "Assert.True(a);\nAssert.Equal(1, b);\n";

        var findings = FakeSuccessScanner.Scan([Modified(_testPath, old, head)]);

        var finding = Assert.Single(findings);
        Assert.Equal(FlagCodes.AssertionRemoved, finding.Code);
        Assert.Equal("Assert. calls 3 -> 2.", finding.Detail);
    }

    [Fact]
    public void ReplacedAssertionWithSameCountIsNotFlagged()
    {
        var findings = FakeSuccessScanner.Scan([Modified(_testPath, "Assert.True(a);\n", "Assert.True(b);\n")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void AddedAssertionIsNotFlagged()
    {
        var findings = FakeSuccessScanner.Scan([Modified(_testPath, "Assert.True(a);\n", "Assert.True(a);\nAssert.True(b);\n")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void CleanChangeProducesNoFindings()
    {
        var findings = FakeSuccessScanner.Scan(
        [
            Modified("src/Bazaar.Server/Thing.cs", "public int N => 1;\n", "public int N => 2;\n"),
            Added("tests/Bazaar.Server.Tests/NewTests.cs", "[Fact]\npublic void E() { Assert.True(true); }\n"),
        ]);

        Assert.Empty(findings);
    }

    [Fact]
    public void EmptyInputProducesNoFindings()
    {
        Assert.Empty(FakeSuccessScanner.Scan([]));
    }

    private static ChangedFile Added(string path, string newText) => new(path, ChangeKind.Added, null, newText);

    private static ChangedFile Modified(string path, string oldText, string newText) => new(path, ChangeKind.Modified, oldText, newText);

    private static ChangedFile Deleted(string path, string oldText) => new(path, ChangeKind.Deleted, oldText, null);
}
