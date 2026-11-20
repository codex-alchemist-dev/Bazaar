using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.Core.Tests.Commands;

public class CommandRegistryTests
{
    private static Command Local(string id, string mnemonic, string title = "Title") =>
        new(id, title, mnemonic, null, CommandSource.Local);

    private static Command Server(string id, string mnemonic, string title = "Title") =>
        new(id, title, mnemonic, null, CommandSource.Server);

    [Fact]
    public void Load_accepts_distinct_commands_and_keeps_local_first()
    {
        var result = CommandRegistry.Load(
            [Local("a", "open"), Local("b", "close")],
            [Server("s", "reload")]);

        Assert.Empty(result.SkippedServerCommands);
        Assert.Equal(["a", "b", "s"], result.Registry.Commands.Select(c => c.Id));
    }

    [Fact]
    public void Load_throws_on_duplicate_local_id()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Load([Local("a", "one"), Local("a", "two")], []));

        Assert.Contains("'a'", ex.Message);
    }

    [Fact]
    public void Load_throws_on_duplicate_local_mnemonic_ignoring_case()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Load([Local("a", "Open"), Local("b", "open")], []));
    }

    [Fact]
    public void Load_throws_on_duplicate_server_id()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CommandRegistry.Load([Local("a", "one")], [Server("a", "two")]));
    }

    [Fact]
    public void Load_allows_a_mnemonic_that_is_only_a_prefix_of_another()
    {
        var result = CommandRegistry.Load([Local("short", "wc"), Local("long", "wcx")], []);

        Assert.Equal("short", result.Registry.FindByMnemonic("wc")!.Id);
        Assert.Equal("long", result.Registry.FindByMnemonic("wcx")!.Id);
    }

    [Fact]
    public void Load_skips_and_reports_server_command_that_conflicts_with_local_mnemonic()
    {
        var result = CommandRegistry.Load([Local("local", "gc")], [Server("remote", "GC")]);

        Assert.Equal(["local"], result.Registry.Commands.Select(c => c.Id));
        var skipped = Assert.Single(result.SkippedServerCommands);
        Assert.Equal("remote", skipped.Command.Id);
        Assert.Contains("local", skipped.Reason);
    }

    [Fact]
    public void Load_skips_later_server_command_that_conflicts_with_earlier_server_mnemonic()
    {
        var result = CommandRegistry.Load([], [Server("first", "new chat"), Server("second", "new chat")]);

        Assert.Equal(["first"], result.Registry.Commands.Select(c => c.Id));
        Assert.Equal("second", Assert.Single(result.SkippedServerCommands).Command.Id);
    }

    [Fact]
    public void Load_rejects_a_local_command_with_server_source()
    {
        var wrong = Server("x", "mx");

        Assert.Throws<ArgumentException>(() => CommandRegistry.Load([wrong], []));
    }

    [Fact]
    public void Load_rejects_a_server_command_with_local_source()
    {
        var wrong = Local("x", "mx");

        Assert.Throws<ArgumentException>(() => CommandRegistry.Load([], [wrong]));
    }

    [Fact]
    public void FindByMnemonic_is_exact_and_case_insensitive()
    {
        var registry = CommandRegistry.Load([Local("open", "open file")], []).Registry;

        Assert.Equal("open", registry.FindByMnemonic("OPEN FILE")!.Id);
        Assert.Null(registry.FindByMnemonic("open"));
    }

    [Fact]
    public void FindById_returns_match_or_null()
    {
        var registry = CommandRegistry.Load([Local("open", "o")], []).Registry;

        Assert.Equal("o", registry.FindById("open")!.Mnemonic);
        Assert.Null(registry.FindById("missing"));
    }
}
