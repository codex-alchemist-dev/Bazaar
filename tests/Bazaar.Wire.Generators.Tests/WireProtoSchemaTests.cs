using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bazaar.Wire.Generators.Tests;

/// <summary>
/// <c>docs/wire/wire.proto</c> is generated from the <c>[WirePayload]</c> records in Bazaar.Contracts and checked in, because the
/// Protobuf codec compiles it with protoc. This test fails when a record changes without the file following, and rewrites the file
/// when <c>BAZAAR_UPDATE_WIRE_PROTO=1</c> (doc 90 section 5). The field-number rules are append-only; review the diff.
/// </summary>
public class WireProtoSchemaTests
{
    private static string Generate(out List<DiagnosticInfo> diagnostics)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Bazaar.Contracts.Wire.WirePayloadAttribute).Assembly.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Bazaar.Wire.Codec.Protobuf",
            [CSharpSyntaxTree.ParseText("class Empty { }", cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        diagnostics = [];
        var payloads = PayloadModel.Discover(compilation, diagnostics, TestContext.Current.CancellationToken);
        return ProtobufEmitter.Emit(compilation, payloads, diagnostics, TestContext.Current.CancellationToken).Proto;
    }

    private static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "docs", "wire", "wire.proto");
    }

    [Fact]
    public void The_generator_reports_no_problem_with_the_contracts()
    {
        _ = Generate(out var diagnostics);

        Assert.Empty(diagnostics.Select(d => d.Descriptor.Id + " " + d.Argument));
    }

    [Fact]
    public void Checked_in_wire_proto_matches_the_records()
    {
        var expected = Generate(out _);
        var path = SchemaPath();
        if (Environment.GetEnvironmentVariable("BAZAAR_UPDATE_WIRE_PROTO") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }

        var actual = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(
            actual == expected,
            "docs/wire/wire.proto is out of date with the [WirePayload] records. Run: BAZAAR_UPDATE_WIRE_PROTO=1 dotnet test tests/Bazaar.Wire.Generators.Tests, then review the diff (field numbers are append-only).");
    }

    [Fact]
    public void Generation_is_stable()
    {
        Assert.Equal(Generate(out _), Generate(out _));
    }

    [Fact]
    public void The_schema_has_no_any_extension_or_group()
    {
        var text = Generate(out _);

        Assert.DoesNotContain("google.protobuf.Any", text);
        Assert.DoesNotContain("extend ", text);
        Assert.DoesNotContain("extensions ", text);
        Assert.DoesNotContain("group ", text);
        Assert.DoesNotContain("import ", text);
    }

    [Fact]
    public void Field_numbers_follow_declaration_order_from_one()
    {
        var text = Generate(out _);

        Assert.Contains("message GcSend {", text);
        var block = text[text.IndexOf("message GcSend {", StringComparison.Ordinal)..];
        block = block[..block.IndexOf('}', StringComparison.Ordinal)];
        Assert.Contains("= 1;", block);
        Assert.Contains("= 2;", block);
    }
}
