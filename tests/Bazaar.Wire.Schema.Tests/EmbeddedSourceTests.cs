using Bazaar.Wire.Abstractions;
using Bazaar.Wire.Schema.Generated;

namespace Bazaar.Wire.Schema.Tests;

public class EmbeddedSourceTests
{
    [Fact]
    public void Info_matches_the_generated_schema()
    {
        var source = new EmbeddedWireSchemaSource();

        Assert.IsAssignableFrom<IWireSchemaSource>(source);
        Assert.Equal(WireSchemaArtifacts.SchemaHash, source.Info.Hash);
        Assert.Equal(WireSchemaArtifacts.ProtocolMajor, source.Info.Major);
        Assert.Equal(WireSchemaArtifacts.ProtocolMinor, source.Info.Minor);
    }

    [Fact]
    public void Serves_the_three_schema_files_and_the_vectors()
    {
        var source = new EmbeddedWireSchemaSource();

        Assert.True(source.TryGet("schema.json", out var schema));
        Assert.Equal("application/json", schema.ContentType);
        Assert.True(source.TryGet("json-schema.json", out _));
        Assert.True(source.TryGet("wire.cddl", out _));
        Assert.Contains(source.Names, n => n.StartsWith("vectors/cbor/", StringComparison.Ordinal) && n.EndsWith(".bin", StringComparison.Ordinal));
        Assert.Contains(source.Names, n => n.StartsWith("vectors/json/", StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal));
        Assert.False(source.TryGet("nope", out _));
    }

    [Fact]
    public void Embedded_files_are_the_checked_in_ones_after_a_rebuild()
    {
        if (WireFiles.Updating) return;
        var source = new EmbeddedWireSchemaSource();

        Assert.True(source.TryGet("schema.json", out var schema));
        Assert.Equal(WireFiles.Read("schema.lock.json").Replace("\r\n", "\n"), System.Text.Encoding.UTF8.GetString(schema.Content.Span).Replace("\r\n", "\n"));
        Assert.Equal(WireSchemaArtifacts.SchemaHash, source.Info.Hash);
    }
}
