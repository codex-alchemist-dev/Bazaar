using Bazaar.Server.Vault.Index;
using Bazaar.Server.Vault.Markdown;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Tests.Vault.Index;

/// <summary>The owner front-matter value must match the form access compares against (doc 18, "Access").</summary>
public sealed class VaultOwnerFormatTests
{
    private static readonly Guid _vaultId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");

    [Fact]
    public void The_agent_owner_form_round_trips_through_front_matter_and_access()
    {
        var agent = Guid.Parse("0196a7c4-0000-7000-8000-0000000000d4");
        var owner = VaultOwner.ForAgent(agent);

        Assert.Equal("agent:0196a7c4-0000-7000-8000-0000000000d4", owner);
        Assert.True(VaultOwner.IsWellFormed(owner));
        Assert.True(VaultOwner.IsWellFormed(null));
    }

    [Theory]
    [InlineData("Agent-Ana")]
    [InlineData("agent:0196A7C4-0000-7000-8000-0000000000D4")]
    [InlineData("agent:not-a-guid")]
    [InlineData("0196a7c4-0000-7000-8000-0000000000d4")]
    public void Malformed_owners_are_refused_by_the_parser(string owner)
    {
        var source = "---\nid: 01J5Q2R0000000000000000001\ntype: memory\ntitle: Note\nowner: " + owner + "\n---\n\nBody.\n";

        Assert.Throws<VaultFormatException>(() => VaultDocumentParser.TryParse(
            _vaultId, Guid.NewGuid(), "memory/note.md", source, "abc123", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_well_formed_private_owner_parses()
    {
        var owner = VaultOwner.ForAgent(Guid.Parse("0196a7c4-0000-7000-8000-0000000000d4"));
        var source = "---\nid: 01J5Q2R0000000000000000001\ntype: memory\ntitle: Note\nowner: " + owner + "\n---\n\nBody.\n";

        var parsed = VaultDocumentParser.TryParse(_vaultId, Guid.NewGuid(), "memory/note.md", source, "abc123", DateTimeOffset.UnixEpoch);

        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed.Doc.Owner);
    }
}
