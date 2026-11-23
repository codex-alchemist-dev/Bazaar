namespace Bazaar.Client.Athanor.Tests.Documents;

public class EditorDocumentIdsTests
{
    [Fact]
    public void Create_normalises_the_path_into_the_id()
    {
        var id = EditorDocumentIds.Create("w1", "src\\a/./b.cs");

        Assert.Equal(new EditorDocumentId("w1", "src/a/b.cs"), id);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("C:/x")]
    [InlineData("")]
    public void Create_throws_for_a_path_that_fails_normalisation(string raw)
    {
        Assert.Throws<ArgumentException>(() => EditorDocumentIds.Create("w1", raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_throws_for_a_blank_workspace(string workspaceId)
    {
        Assert.Throws<ArgumentException>(() => EditorDocumentIds.Create(workspaceId, "a.txt"));
    }

    [Fact]
    public void Create_throws_for_a_null_path()
    {
        Assert.Throws<ArgumentException>(() => EditorDocumentIds.Create("w1", null!));
    }
}
