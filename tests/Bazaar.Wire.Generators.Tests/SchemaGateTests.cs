using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Bazaar.Wire.Generators.Tests;

/// <summary>The schema artifacts and the lock gate in the generator (doc 90 sections 3 and 5): breaking changes fail the build with BWG007.</summary>
public class SchemaGateTests
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
            public static class WireProtocol { public const int Major = 1; public const int Minor = 0; }
        }
        """;

    private sealed class LockText(string text) : AdditionalText
    {
        public override string Path => "/repo/docs/wire/schema.lock.json";

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private static CSharpCompilation Compile(string source, string? protocol = null)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var marker = protocol is null ? _marker : _marker.Replace("Major = 1", $"Major = {protocol}");
        return CSharpCompilation.Create(
            "Bazaar.Test",
            [
                CSharpSyntaxTree.ParseText(marker, cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText("using Bazaar.Contracts.Wire;\n[assembly: Bazaar.Wire.Generators.GenerateWireCodec(\"schema\")]\n" + source, cancellationToken: TestContext.Current.CancellationToken),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static string SchemaJsonOf(string source)
    {
        var compilation = Compile(source);
        var diagnostics = new List<DiagnosticInfo>();
        var payloads = PayloadModel.Discover(compilation, diagnostics, TestContext.Current.CancellationToken);
        var doc = SchemaBuilder.Build(compilation, payloads, diagnostics, TestContext.Current.CancellationToken);
        doc.Hash = SchemaEmitters.ComputeHash(doc);
        return SchemaEmitters.SchemaJson(doc, doc.Hash);
    }

    private static ImmutableArray<Diagnostic> RunAgainstLock(string lockSource, string newSource, string? protocol = null)
    {
        var lockJson = SchemaJsonOf(lockSource);
        var driver = CSharpGeneratorDriver.Create([new WireCodecGenerator().AsSourceGenerator()], [new LockText(lockJson)]);
        return driver.RunGenerators(Compile(newSource, protocol), TestContext.Current.CancellationToken).GetRunResult().Diagnostics;
    }

    private const string _v1 = """
        public enum Kind { Unknown = 0, A = 1, B = 2 }
        [WirePayload("a.one")] public sealed record A(int X, string Name, Kind Kind);
        """;

    [Fact]
    public void Unchanged_schema_reports_nothing()
    {
        Assert.Empty(RunAgainstLock(_v1, _v1));
    }

    [Fact]
    public void Appending_a_nullable_or_defaulted_field_is_compatible()
    {
        var diagnostics = RunAgainstLock(_v1, _v1.Replace("Kind Kind)", "Kind Kind, string? Extra, int More = 3)"));

        Assert.DoesNotContain(diagnostics, d => d.Id == "BWG007");
        Assert.Contains(diagnostics, d => d.Id == "BWG008" && d.Severity == DiagnosticSeverity.Info);
    }

    [Theory]
    [InlineData("int X, string Name, Kind Kind, string Extra)", "not nullable and has no default")]
    [InlineData("string Name, int X, Kind Kind)", "reordered")]
    [InlineData("int X, Kind Kind)", "removed")]
    [InlineData("long X, string Name, Kind Kind)", "type changed")]
    [InlineData("int X, string? Name, Kind Kind)", "type changed")]
    [InlineData("int Y, string Name, Kind Kind)", "renamed")]
    public void Breaking_field_changes_fail_the_build(string newFields, string expectedText)
    {
        var diagnostics = RunAgainstLock(_v1, _v1.Replace("int X, string Name, Kind Kind)", newFields));

        var error = Assert.Single(diagnostics, d => d.Id == "BWG007");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(expectedText, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("major bump", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Removing_a_message_type_or_an_enum_member_fails_the_build()
    {
        Assert.Contains(RunAgainstLock(_v1, _v1.Replace("a.one", "a.two")), d => d.Id == "BWG007" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("a.one"));
        Assert.Contains(RunAgainstLock(_v1, _v1.Replace("B = 2", "C = 3")), d => d.Id == "BWG007" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("'b'"));
        Assert.Contains(RunAgainstLock(_v1, _v1.Replace("B = 2", "B = 5")), d => d.Id == "BWG007" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("changed value"));
    }

    [Fact]
    public void A_protocol_major_bump_allows_breaking_changes_but_still_wants_a_new_lock()
    {
        var diagnostics = RunAgainstLock(_v1, _v1.Replace("int X, string Name, Kind Kind)", "string Name, int X, Kind Kind)"), protocol: "2");

        Assert.DoesNotContain(diagnostics, d => d.Id == "BWG007");
        Assert.Contains(diagnostics, d => d.Id == "BWG008" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("protocol version is 2.0"));
    }

    [Fact]
    public void Garbage_lock_is_reported()
    {
        var driver = CSharpGeneratorDriver.Create([new WireCodecGenerator().AsSourceGenerator()], [new LockText("{ nope")]);
        var diagnostics = driver.RunGenerators(Compile(_v1), TestContext.Current.CancellationToken).GetRunResult().Diagnostics;

        Assert.Contains(diagnostics, d => d.Id == "BWG009");
    }

    [Fact]
    public void Schema_text_is_stable_and_round_trips_through_the_lock_reader()
    {
        var first = SchemaJsonOf(_v1);

        Assert.Equal(first, SchemaJsonOf(_v1));
        var doc = MiniJson.ToDoc(first);
        Assert.NotNull(doc);
        Assert.Equal(SchemaEmitters.ComputeHash(doc), doc.Hash);
        var a = Assert.Single(doc.Types, t => t.Name == "A");
        Assert.Equal(["x", "name", "kind"], a.Fields.Select(f => f.Name).ToArray());
        Assert.Equal([1, 2, 3], a.Fields.Select(f => f.Position).ToArray());
        Assert.Equal("unknown", Assert.Single(doc.Types, t => t.Name == "Kind").Fallback);
    }

    [Fact]
    public void Artifacts_cover_json_schema_and_cddl()
    {
        var compilation = Compile(_v1 + "\n[WirePayload] public sealed record B(System.Guid Id, System.Collections.Generic.IReadOnlyList<A> Items, float[] Vec, System.DateTimeOffset At, decimal? Price);");
        var diagnostics = new List<DiagnosticInfo>();
        var payloads = PayloadModel.Discover(compilation, diagnostics, TestContext.Current.CancellationToken);
        var doc = SchemaBuilder.Build(compilation, payloads, diagnostics, TestContext.Current.CancellationToken);

        var jsonSchema = SchemaEmitters.JsonSchema(doc, "h");
        var cddl = SchemaEmitters.Cddl(doc, "h");

        Assert.Empty(diagnostics);
        Assert.Contains("\"format\": \"uuid\"", jsonSchema);
        Assert.Contains("\"$ref\": \"#/$defs/A\"", jsonSchema);
        Assert.Contains("\"required\": [\"x\", \"name\", \"kind\"]", jsonSchema);
        Assert.Contains("id: uuid-text", cddl);
        Assert.Contains("vec: float32-array", cddl);
        Assert.Contains("at: #6.0(tstr)", cddl);
        Assert.Contains("? price: decimal-fraction", cddl);
        Assert.Contains("items: [* a]", cddl);
        Assert.Contains("kind = \"unknown\" / \"a\" / \"b\"", cddl);
    }
}
