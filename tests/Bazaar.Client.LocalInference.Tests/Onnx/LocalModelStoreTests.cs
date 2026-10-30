using Bazaar.Client.LocalInference.Onnx;

namespace Bazaar.Client.LocalInference.Tests.Onnx;

public class LocalModelStoreTests
{
    private static readonly ModelManifest Manifest = new("identity", "0.0.1", "Apache-2.0", [], "1.31.1", null);

    [Fact]
    public void Resolves_model_files_under_model_and_version()
    {
        var store = new LocalModelStore(Path.Combine(Path.GetTempPath(), "store"));

        var path = store.ResolveFile(Manifest, new ManifestFile("identity.onnx", "", 139));

        Assert.Equal(Path.Combine(store.Root, "identity", "0.0.1", "identity.onnx"), path);
    }

    [Theory]
    [InlineData("../escape.onnx")]
    [InlineData("nested/identity.onnx")]
    [InlineData("..")]
    public void Refuses_file_names_that_are_not_a_single_path_segment(string fileName)
    {
        var store = new LocalModelStore(Path.Combine(Path.GetTempPath(), "store"));

        Assert.Throws<ArgumentException>(() => store.ResolveFile(Manifest, new ManifestFile(fileName, "", 0)));
    }
}
