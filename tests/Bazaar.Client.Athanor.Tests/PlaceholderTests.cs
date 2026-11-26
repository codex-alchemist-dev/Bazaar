namespace Bazaar.Client.Athanor.Tests;

public class DocumentIdTests
{
    [Fact]
    public void Id_prints_workspace_and_path() =>
        Assert.Equal("w1:src/a.cs", new EditorDocumentId("w1", "src/a.cs").ToString());
}
