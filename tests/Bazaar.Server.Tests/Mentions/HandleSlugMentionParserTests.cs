using Bazaar.Server.Mentions;

namespace Bazaar.Server.Tests.Mentions;

/// <summary>A handle slug, found by the agent mention parser. The pure handle rules live in Bazaar.Server.Mentions.Tests.</summary>
public sealed class HandleSlugMentionParserTests
{
    [Fact]
    public void Handles_with_separators_are_found_by_the_mention_parser()
    {
        // The parser and the handle shape must agree: "_" and "-" inside a handle do not end it early.
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var members = new[]
        {
            new Bazaar.Server.Agents.Turns.Posting.GcMentionMember(other, "ada"),
            new Bazaar.Server.Agents.Turns.Posting.GcMentionMember(id, "ada-lovelace_2"),
        };

        var scan = Bazaar.Server.Agents.Turns.Posting.AgentMentionParser.Scan("ping @Ada-Lovelace_2, please", members);

        var span = Assert.Single(scan.Spans);
        Assert.Equal(id, span.PrincipalId);
    }
}
