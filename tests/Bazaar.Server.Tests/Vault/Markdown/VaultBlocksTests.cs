using System.Text;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Markdown;

public class VaultBlocksTests
{
    [Fact]
    public void Front_matter_is_not_a_block_and_blocks_are_numbered_in_order()
    {
        const string source = "---\nid: 01JABCDEFGHJKMNPQRSTVWXYZ0\ntype: research\ntitle: T\n---\n# One\n\nFirst paragraph.\n\n## Two\n\n- item\n";

        var blocks = VaultBlocks.Split(source);

        Assert.Equal([0, 1, 2, 3], blocks.Select(block => block.Ordinal));
        Assert.StartsWith("# One", blocks[0].Body, StringComparison.Ordinal);
        Assert.Equal("First paragraph.", blocks[1].Body);
        Assert.Equal("## Two", blocks[2].Body);
    }

    [Fact]
    public void Heading_path_lists_the_enclosing_headings_and_includes_a_heading_itself()
    {
        const string source = "# Top\n\nIntro.\n\n## Child\n\nDetail.\n\n# Next\n\nText.\n";

        var blocks = VaultBlocks.Split(source);

        Assert.Equal(["Top"], blocks[0].HeadingPath);
        Assert.Equal(["Top"], blocks[1].HeadingPath);
        Assert.Equal(["Top", "Child"], blocks[3].HeadingPath);
        Assert.Equal(["Next"], blocks[5].HeadingPath);
    }

    [Fact]
    public void Trailing_block_id_is_read_from_the_last_line()
    {
        const string source = "A line that ends with the marker ^abc-1\n\nNo marker here.\n";

        var blocks = VaultBlocks.Split(source);

        Assert.Equal("abc-1", blocks[0].BlockId);
        Assert.Null(blocks[1].BlockId);
    }

    [Fact]
    public void Offsets_and_lines_are_absolute_and_one_based()
    {
        const string source = "---\nid: 01JABCDEFGHJKMNPQRSTVWXYZ0\ntype: research\ntitle: T\n---\nfirst\n\nsecond\nthird\n";

        var blocks = VaultBlocks.Split(source);

        Assert.Equal(source.IndexOf("first", StringComparison.Ordinal), blocks[0].Start);
        Assert.Equal(6, blocks[0].StartLine);
        Assert.Equal(8, blocks[1].StartLine);
        Assert.Equal(9, blocks[1].EndLine);
        Assert.Equal(1, VaultBlocks.OrdinalAt(blocks, blocks[1].Start));
        Assert.Equal(-1, VaultBlocks.OrdinalAt(blocks, 0));
    }

    [Fact]
    public void Text_hash_depends_only_on_the_block_text()
    {
        var before = VaultBlocks.Split("Same text.\n\nOther.\n");
        var after = VaultBlocks.Split("Changed header line.\n\nSame text.\n");

        Assert.Equal(before[0].TextHash, after[1].TextHash);
        Assert.NotEqual(before[0].TextHash, after[0].TextHash);
        Assert.Equal(32, before[0].TextHash.Length);
    }

    [Fact]
    public void Crlf_documents_keep_their_offsets()
    {
        var source = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("# H\r\n\r\nBody.\r\n"));

        var blocks = VaultBlocks.Split(source);

        Assert.Equal("Body.", blocks[1].Body);
        Assert.Equal(3, blocks[1].StartLine);
    }
}
