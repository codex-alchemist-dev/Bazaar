using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>Pure checks of which tools a turn offers, and of the request rules that need no database.</summary>
public sealed class TurnToolOfferTests
{
    private static readonly ModelToolDefinition _read = new("read_note", "Reads a note.", """{"type":"object"}""");
    private static readonly ModelToolDefinition _write = new("write_note", "Writes a note.", """{"type":"object"}""");
    private static readonly ModelToolDefinition _post = new("post_message", "Posts to a GC.", """{"type":"object"}""");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_actor_turn_is_offered_the_catalogue_entries_its_allowlist_names_in_catalogue_order()
    {
        var offered = TurnToolOffer.Select(TurnRole.Actor, [_read, _write, _post], ["post_message", "read_note"]);

        Assert.Equal(["read_note", "post_message"], offered.Select(t => t.Name));
        Assert.Equal(_read, offered[0]);
        Assert.Equal(_post, offered[1]);
    }

    [Fact]
    public void An_actor_turn_with_an_empty_allowlist_is_offered_no_tools()
    {
        Assert.Empty(TurnToolOffer.Select(TurnRole.Actor, [_read, _write], []));
    }

    [Fact]
    public void An_allowlist_name_missing_from_the_catalogue_is_ignored()
    {
        var offered = TurnToolOffer.Select(TurnRole.Actor, [_read], ["write_note"]);

        Assert.Empty(offered);
    }

    [Fact]
    public void A_reader_turn_is_offered_no_tools_even_when_the_allowlist_names_them()
    {
        var offered = TurnToolOffer.Select(TurnRole.Reader, [_read, _write], ["read_note", "write_note"]);

        Assert.Empty(offered);
    }

    [Fact]
    public void ForRole_passes_supplied_tools_to_an_actor_and_none_to_a_reader()
    {
        Assert.Equal([_read, _write], TurnToolOffer.ForRole(TurnRole.Actor, [_read, _write]));
        Assert.Empty(TurnToolOffer.ForRole(TurnRole.Reader, [_read, _write]));
    }

    [Fact]
    public void ForRole_with_no_supplied_list_offers_nothing()
    {
        Assert.Empty(TurnToolOffer.ForRole(TurnRole.Actor, null));
    }

    [Fact]
    public async Task A_request_that_names_items_and_also_asks_for_a_count_is_refused_before_any_database_use()
    {
        // The argument check runs before the lease is taken, so an offline data source is enough.
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");
        var runner = new TurnRunner(source, new ScriptedModelGateway(), new RecordingToolDispatcher());
        var request = new TurnRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "mention",
            "agent-default",
            [new ModelMessage(ModelRole.User, "hi")],
            InboxClaimLimit: 2,
            InboxItemIds: [Guid.CreateVersion7()]);

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(request, Ct));
    }
}
