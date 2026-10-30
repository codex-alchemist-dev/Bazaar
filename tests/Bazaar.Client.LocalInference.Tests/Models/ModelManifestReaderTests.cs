using Bazaar.Client.LocalInference.Models;

namespace Bazaar.Client.LocalInference.Tests.Models;

public class ModelManifestReaderTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ModelManifest Valid() => new(
        "phi-4-mini",
        "1.0.0",
        "MIT",
        [new ManifestFile("model.onnx", Sha, 1024), new ManifestFile("tokenizer.json", Sha, 0)],
        "1.0.0",
        "sig-A");

    [Fact]
    public void Valid_manifest_survives_a_serialize_and_parse_round_trip()
    {
        var original = Valid();

        var parsed = ModelManifestReader.Parse(ModelManifestReader.Serialize(original));

        Assert.Equal(original.Model, parsed.Model);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.License, parsed.License);
        Assert.Equal(original.MinRuntime, parsed.MinRuntime);
        Assert.Equal(original.Signature, parsed.Signature);
        Assert.Equal(original.Files.Count, parsed.Files.Count);
        for (var i = 0; i < original.Files.Count; i++)
        {
            Assert.Equal(original.Files[i], parsed.Files[i]);
        }
    }

    [Fact]
    public void Parses_the_camel_case_manifest_sketch_from_doc_31()
    {
        const string json = """
            {
              "model": "phi-4-mini",
              "version": "1.0.0",
              "license": "MIT",
              "files": [{ "name": "model.onnx", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "bytes": 42 }],
              "minRuntime": "1.20.0",
              "signature": "sig-A"
            }
            """;

        var manifest = ModelManifestReader.Parse(json);

        Assert.Equal("phi-4-mini", manifest.Model);
        Assert.Equal("1.20.0", manifest.MinRuntime);
        Assert.Equal("sig-A", manifest.Signature);
        Assert.Equal(42, manifest.Files[0].Bytes);
    }

    [Fact]
    public void Unsigned_manifest_omits_the_signature_and_parses_with_null()
    {
        var unsigned = Valid() with { Signature = null };

        var json = ModelManifestReader.Serialize(unsigned);
        var parsed = ModelManifestReader.Parse(json);

        Assert.DoesNotContain("signature", json);
        Assert.Null(parsed.Signature);
    }

    [Fact]
    public void Invalid_json_is_rejected()
    {
        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Parse("{ not json"));
    }

    [Fact]
    public void Unknown_property_is_rejected()
    {
        const string json = """
            {"model":"m","version":"1","license":"MIT","files":[{"name":"a.bin","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","bytes":1}],"minRuntime":"1","extra":true}
            """;

        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Parse(json));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/file.bin")]
    [InlineData("..")]
    [InlineData("manifest.json")]
    public void File_name_that_is_not_a_plain_name_is_rejected(string name)
    {
        var manifest = Valid() with { Files = [new ManifestFile(name, Sha, 1)] };

        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(manifest));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Model_id_or_version_that_is_not_a_plain_identifier_is_rejected(string value)
    {
        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(Valid() with { Model = value }));
        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(Valid() with { Version = value }));
    }

    [Theory]
    [InlineData("nothex")]
    [InlineData("0123")]
    [InlineData("")]
    public void Sha256_that_is_not_64_hex_characters_is_rejected(string sha)
    {
        var manifest = Valid() with { Files = [new ManifestFile("a.bin", sha, 1)] };

        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(manifest));
    }

    [Fact]
    public void Negative_byte_length_is_rejected()
    {
        var manifest = Valid() with { Files = [new ManifestFile("a.bin", Sha, -1)] };

        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(manifest));
    }

    [Fact]
    public void Duplicate_file_names_are_rejected_case_insensitively()
    {
        var manifest = Valid() with { Files = [new ManifestFile("Model.onnx", Sha, 1), new ManifestFile("model.ONNX", Sha, 1)] };

        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(manifest));
    }

    [Fact]
    public void Manifest_without_files_is_rejected()
    {
        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(Valid() with { Files = [] }));
    }

    [Fact]
    public void Blank_signature_is_rejected_because_unsigned_means_absent()
    {
        Assert.Throws<ModelFormatException>(() => ModelManifestReader.Validate(Valid() with { Signature = "   " }));
    }
}
