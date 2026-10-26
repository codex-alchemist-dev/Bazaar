using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Markdown;

/// <summary>Compares the links found in each golden document with its hand-written <c>.links.json</c>.</summary>
public class VaultMarkdownGoldenTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [MemberData(nameof(GoldenFixtures.Names), MemberType = typeof(GoldenFixtures))]
    public void Links_match_the_hand_written_expectation(string name)
    {
        // File.ReadAllText keeps the CRLF bytes of crlf.md, so offsets are checked against the real file.
        var source = File.ReadAllText(GoldenFixtures.Document(name));
        var expected = JsonSerializer.Deserialize<List<WikiLink>>(File.ReadAllText(GoldenFixtures.ExpectedLinks(name)), Options);

        var document = VaultMarkdown.Parse(source);

        Assert.Equal(source, document.Source);
        Assert.Null(document.FrontMatter);
        Assert.Equal(expected, document.Links);
    }

    [Fact]
    public void Every_golden_document_has_an_expectation_file()
    {
        var names = GoldenFixtures.Names().Select(row => (string)row[0]).ToList();

        Assert.True(names.Count >= 6, "expected at least the forms, containers, not-links, front-matter, code and crlf fixtures");
        foreach (var name in names)
        {
            Assert.True(File.Exists(GoldenFixtures.ExpectedLinks(name)), $"{name}.links.json is missing");
        }
    }
}
