using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bazaar.Wire.Generators.Tests;

public class WireCodecGeneratorTests
{
    private const string _marker = """
        namespace Bazaar.Contracts.Wire
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple = true)]
            public sealed class WirePayloadAttribute : System.Attribute
            {
                public WirePayloadAttribute() { }
                public WirePayloadAttribute(string messageType) { MessageType = messageType; }
                public string? MessageType { get; }
            }
        }
        """;

    private const string _optIn = "[assembly: Bazaar.Wire.Generators.GenerateWireCodec(\"cbor\")]";

    private static (ImmutableArray<Diagnostic> Diagnostics, string[] Files, string Text) Run(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Bazaar.Test",
            [CSharpSyntaxTree.ParseText(_marker, cancellationToken: TestContext.Current.CancellationToken), CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new WireCodecGenerator()).RunGenerators(compilation, TestContext.Current.CancellationToken);
        var result = driver.GetRunResult();
        var sources = result.Results.SelectMany(r => r.GeneratedSources).ToArray();
        return (result.Diagnostics, sources.Select(s => s.HintName).ToArray(), string.Join("\n", sources.Select(s => s.SourceText.ToString())));
    }

    [Fact]
    public void Without_the_opt_in_attribute_only_the_attribute_is_emitted()
    {
        var (diagnostics, files, _) = Run("using Bazaar.Contracts.Wire; [WirePayload(\"a\")] public sealed record A(int X);");

        Assert.Empty(diagnostics);
        Assert.Equal(["GenerateWireCodecAttribute.g.cs"], files);
    }

    [Fact]
    public void Registry_lists_payloads_with_message_types_and_uses_camel_case_keys()
    {
        var (diagnostics, files, text) = Run($$"""
            using Bazaar.Contracts.Wire;
            {{_optIn}}
            [WirePayload("a.one")] [WirePayload("a.two")] public sealed record A(int FirstValue, string? Name);
            [WirePayload] public sealed record B(System.Guid GcId, System.Collections.Generic.IReadOnlyList<A> Items);
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("WireCbor.g.cs", files);
        Assert.Contains("\"a.one\", \"a.two\"", text);
        Assert.Contains("\"firstValue\"", text);
        Assert.Contains("\"gcId\"", text);
        Assert.Contains("typeof(global::B)", text);
        Assert.Contains("namespace Bazaar.Test.Generated;", text);
    }

    [Fact]
    public void Output_is_stable_between_runs()
    {
        const string Source = "using Bazaar.Contracts.Wire;\n" + _optIn + "\n[WirePayload] public sealed record A(int X); [WirePayload] public sealed record B(string Y);";

        Assert.Equal(Run(Source).Text, Run(Source).Text);
    }

    [Fact]
    public void An_unsupported_property_type_is_reported()
    {
        var (diagnostics, _, _) = Run($"using Bazaar.Contracts.Wire;\n{_optIn}\n[WirePayload] public sealed record A(System.Collections.Generic.Dictionary<string, string> Bag);");

        Assert.Contains(diagnostics, d => d.Id == "BWG003");
    }

    [Fact]
    public void The_same_message_type_on_two_records_is_reported()
    {
        var (diagnostics, _, _) = Run($"using Bazaar.Contracts.Wire;\n{_optIn}\n[WirePayload(\"x\")] public sealed record A(int X); [WirePayload(\"x\")] public sealed record B(int Y);");

        Assert.Contains(diagnostics, d => d.Id == "BWG006");
    }

    [Fact]
    public void An_unknown_codec_name_is_reported()
    {
        var (diagnostics, _, _) = Run("[assembly: Bazaar.Wire.Generators.GenerateWireCodec(\"nope\")]");

        Assert.Contains(diagnostics, d => d.Id == "BWG001");
    }

    [Fact]
    public void A_missing_contracts_marker_is_reported()
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create("NoContracts", [CSharpSyntaxTree.ParseText(_optIn, cancellationToken: TestContext.Current.CancellationToken)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = CSharpGeneratorDriver.Create(new WireCodecGenerator()).RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();

        Assert.Contains(result.Diagnostics, d => d.Id == "BWG002");
    }

    [Theory]
    [InlineData("GcId")]
    [InlineData("S")]
    [InlineData("N")]
    [InlineData("Ev")]
    [InlineData("ReplyToN")]
    [InlineData("UiRevision")]
    [InlineData("MaxX")]
    [InlineData("NodeIDs")]
    [InlineData("URL")]
    [InlineData("XYZ")]
    [InlineData("already")]
    public void Camel_casing_matches_system_text_json(string name)
    {
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(name), CborEmitter.Camel(name));
    }
}
