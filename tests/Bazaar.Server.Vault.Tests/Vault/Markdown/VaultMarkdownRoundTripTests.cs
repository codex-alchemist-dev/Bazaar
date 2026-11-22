using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Markdown;

/// <summary>Checks that rendering keeps every byte that no edit touches (doc 18, "Testing": round-trip).</summary>
public class VaultMarkdownRoundTripTests
{
    [Theory]
    [MemberData(nameof(GoldenFixtures.Names), MemberType = typeof(GoldenFixtures))]
    public void Unchanged_document_renders_byte_identical(string name)
    {
        var source = File.ReadAllText(GoldenFixtures.Document(name));

        var document = VaultMarkdown.Parse(source);

        Assert.Equal(source, document.Render([]));
    }

    [Theory]
    [MemberData(nameof(GoldenFixtures.Names), MemberType = typeof(GoldenFixtures))]
    public void Editing_one_link_changes_only_its_span(string name)
    {
        var source = File.ReadAllText(GoldenFixtures.Document(name));
        var document = VaultMarkdown.Parse(source);

        foreach (var link in document.Links)
        {
            var span = source.Substring(link.Start, link.Length);
            Assert.StartsWith(link.Kind == WikiLinkKind.Embed ? "![[" : "[[", span, StringComparison.Ordinal);
            Assert.EndsWith("]]", span, StringComparison.Ordinal);

            var expected = source[..link.Start] + "@@" + source[(link.Start + link.Length)..];
            var actual = document.Render([new SourceEdit(link.Start, link.Length, "@@")]);

            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [MemberData(nameof(GoldenFixtures.Names), MemberType = typeof(GoldenFixtures))]
    public void Editing_every_link_changes_only_the_link_spans(string name)
    {
        var source = File.ReadAllText(GoldenFixtures.Document(name));
        var document = VaultMarkdown.Parse(source);
        var edits = document.Links.Select(link => new SourceEdit(link.Start, link.Length, "@@")).ToList();

        var expected = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var link in document.Links)
        {
            expected.Append(source, cursor, link.Start - cursor).Append("@@");
            cursor = link.Start + link.Length;
        }

        expected.Append(source, cursor, source.Length - cursor);
        Assert.Equal(expected.ToString(), document.Render(edits));
    }

    [Fact]
    public void Render_rejects_overlapping_edits()
    {
        var source = File.ReadAllText(GoldenFixtures.Document("forms"));
        var document = VaultMarkdown.Parse(source);
        var link = document.Links[1];

        var overlapping = new[]
        {
            new SourceEdit(link.Start, link.Length, "a"),
            new SourceEdit(link.Start + 1, link.Length, "b"),
        };

        Assert.Throws<ArgumentException>(() => document.Render(overlapping));
    }
}
