namespace Bazaar.Server.RepoMap.Tests;

public sealed class RoslynCSharpSymbolExtractorTests
{
    private const string _sample = """
        using System;

        namespace _sample;

        [Serializable]
        public sealed class Calculator : IDisposable
        {
            private int _total;

            public Calculator(int start)
            {
                _total = start;
            }

            public static int Add(int a, int b) => a + b;

            public int Total { get; private set; }

            public void Dispose() { }

            public class Inner
            {
                public string Label([Attr] string prefix, int count = 2) { return prefix; }
            }
        }

        public interface IShape { double Area(); }

        public struct Point { public int X; }

        public record Person(string Name);

        public enum Colour { Red, Green }

        public delegate void Handler(object sender);
        """;

    private static Task<IReadOnlyList<CodeSymbol>> Extract(string content) =>
        new RoslynCSharpSymbolExtractor().ExtractAsync("src/_sample.cs", content, CancellationToken.None);

    [Fact]
    public async Task Recognises_each_declaration_kind_with_its_name()
    {
        var symbols = await Extract(_sample);

        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Class, Name: "Calculator" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Class, Name: "Inner" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Interface, Name: "IShape" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Struct, Name: "Point" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Record, Name: "Person" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Enum, Name: "Colour" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Delegate, Name: "Handler" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Constructor, Name: "Calculator" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Method, Name: "Add" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Method, Name: "Label" });
        Assert.Contains(symbols, s => s is { Kind: SymbolKind.Property, Name: "Total" });
    }

    [Fact]
    public async Task Signatures_carry_modifiers_return_types_and_parameters()
    {
        var symbols = await Extract(_sample);

        Assert.Equal("public static int Add(int a, int b)", symbols.Single(s => s.Name == "Add").Signature);
        Assert.Equal("public Calculator(int start)", symbols.Single(s => s.Kind == SymbolKind.Constructor).Signature);
        Assert.Equal("public int Total", symbols.Single(s => s.Name == "Total").Signature);
        Assert.Equal("public enum Colour", symbols.Single(s => s.Name == "Colour").Signature);
    }

    [Fact]
    public async Task Attributes_are_not_part_of_the_signature()
    {
        var symbols = await Extract(_sample);

        var label = symbols.Single(s => s.Name == "Label");
        Assert.DoesNotContain("Attr", label.Signature);
        Assert.Equal("public string Label(string prefix, int count = 2)", label.Signature);
        Assert.DoesNotContain("Serializable", symbols.Single(s => s.Name == "Calculator" && s.Kind == SymbolKind.Class).Signature);
    }

    [Fact]
    public async Task Line_numbers_point_at_the_identifier()
    {
        var symbols = await Extract(_sample);

        Assert.Equal(6, symbols.Single(s => s.Name == "Calculator" && s.Kind == SymbolKind.Class).Line);
        Assert.Equal(10, symbols.Single(s => s.Kind == SymbolKind.Constructor).Line);
        Assert.Equal(15, symbols.Single(s => s.Name == "Add").Line);
    }

    [Fact]
    public async Task Nested_types_are_included_in_source_order()
    {
        var symbols = await Extract(_sample);

        var lines = symbols.Select(s => s.Line).ToList();
        Assert.Equal(lines.OrderBy(l => l).ToList(), lines);
        Assert.Contains(symbols, s => s.Name == "Inner");
    }

    [Fact]
    public async Task Malformed_source_still_yields_recognisable_declarations()
    {
        var symbols = await Extract("public class Broken\n{\n    public void Ok() { \n    public int Half(\n");

        Assert.Contains(symbols, s => s.Name == "Broken");
        Assert.Contains(symbols, s => s.Name == "Ok");
    }

    [Fact]
    public async Task Empty_source_yields_no_symbols()
    {
        var symbols = await Extract(string.Empty);

        Assert.Empty(symbols);
    }

    [Fact]
    public async Task Signatures_are_one_line_and_capped()
    {
        var longParams = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"int parameter{i}"));
        var symbols = await Extract($"class Wide {{ public void Huge(\n{longParams}) {{ }} }}");

        var huge = symbols.Single(s => s.Name == "Huge");
        Assert.DoesNotContain('\n', huge.Signature);
        Assert.True(huge.Signature.Length <= 200);
        Assert.EndsWith("...", huge.Signature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbols_per_file_are_capped()
    {
        var body = string.Concat(Enumerable.Range(0, RoslynCSharpSymbolExtractor.MaxSymbolsPerFile + 100).Select(i => $"public void M{i}() {{ }}\n"));

        var symbols = await Extract("class Many {\n" + body + "}\n");

        Assert.Equal(RoslynCSharpSymbolExtractor.MaxSymbolsPerFile, symbols.Count);
    }

    [Theory]
    [InlineData("src/Thing.cs", true)]
    [InlineData("src/Thing.CS", true)]
    [InlineData("src/Thing.txt", false)]
    [InlineData("src/Thing.luau", false)]
    public void Supports_only_csharp_files(string path, bool expected)
    {
        Assert.Equal(expected, new RoslynCSharpSymbolExtractor().Supports(path));
    }
}
