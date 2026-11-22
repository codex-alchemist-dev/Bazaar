using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Enforcers.Tests;

public class NftablesEgressEnforcerTests
{
    private sealed class FakeRoot : IRootCommandRunner
    {
        public List<RootCommand> Commands { get; } = [];

        public Func<RootCommand, HostCommandResult> Handler { get; set; } = _ => new HostCommandResult(0, string.Empty, string.Empty);

        public TimeSpan Delay { get; set; }

        public async Task<HostCommandResult> RunAsync(RootCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            return Handler(command);
        }
    }

    // Echoes the loaded ruleset back for the list call, the way nft prints it, with named priority and indentation changes.
    private static FakeRoot Echo()
    {
        var root = new FakeRoot();
        string loaded = string.Empty;
        root.Handler = c =>
        {
            if (c.Arguments[0] == "-f")
            {
                loaded = c.StandardInput;
                return new HostCommandResult(0, string.Empty, string.Empty);
            }

            var listing = string.Join('\n', loaded.Split('\n')
                .Where(l => !l.StartsWith("add table", StringComparison.Ordinal) && !l.StartsWith("delete table", StringComparison.Ordinal))
                .Select(l => l.Replace("priority -200", "priority filter", StringComparison.Ordinal)));
            return new HostCommandResult(0, listing, string.Empty);
        };
        return root;
    }

    private static NftablesEgressEnforcer Create(FakeRoot root, string? iface = "tap-a1") =>
        new(new FakeResolver(new EgressTarget(["vm"], iface)), root);

    [Fact]
    public async Task Apply_loads_the_generated_ruleset_on_stdin_with_fixed_arguments_then_lists_the_table()
    {
        var root = Echo();

        await Create(root).ApplyAsync("a1", Samples.Rules());

        Assert.Equal(2, root.Commands.Count);
        Assert.Equal(["-f", "-"], root.Commands[0].Arguments);
        Assert.Equal(NftablesRuleset.Generate("a1", "tap-a1", Samples.Rules()), root.Commands[0].StandardInput);
        Assert.Equal(["list", "table", "bridge", "bazaar_egress_a1"], root.Commands[1].Arguments);
    }

    [Fact]
    public async Task Apply_replaces_atomically_because_the_ruleset_deletes_the_old_table_in_the_same_load()
    {
        var root = Echo();

        await Create(root).ApplyAsync("a1", Samples.Rules());

        var text = root.Commands[0].StandardInput;
        Assert.True(text.IndexOf("add table bridge bazaar_egress_a1", StringComparison.Ordinal) < text.IndexOf("delete table bridge bazaar_egress_a1", StringComparison.Ordinal));
        Assert.Single(root.Commands, c => c.Arguments[0] == "-f");
    }

    [Fact]
    public async Task BlockAll_loads_the_block_all_ruleset_and_verifies_it()
    {
        var root = Echo();

        await Create(root).BlockAllAsync("a1");

        Assert.Equal(NftablesRuleset.GenerateBlockAll("a1", "tap-a1"), root.Commands[0].StandardInput);
        Assert.DoesNotContain("accept\n    iifname", root.Commands[0].StandardInput);
        Assert.Equal(2, root.Commands.Count);
    }

    [Fact]
    public async Task Apply_throws_when_the_listing_differs()
    {
        var root = Echo();
        var echo = root.Handler;
        root.Handler = c => c.Arguments[0] == "list"
            ? new HostCommandResult(0, echo(c).StandardOutput.Replace("drop", "accept", StringComparison.Ordinal), string.Empty)
            : echo(c);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).BlockAllAsync("a1"));
    }

    [Fact]
    public async Task Apply_throws_when_the_listing_is_empty_or_the_table_is_missing()
    {
        var root = Echo();
        var echo = root.Handler;
        root.Handler = c => c.Arguments[0] == "list" ? new HostCommandResult(0, string.Empty, string.Empty) : echo(c);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).ApplyAsync("a1", Samples.Rules()));
    }

    [Fact]
    public async Task Apply_throws_when_the_load_fails_and_does_not_list()
    {
        var root = new FakeRoot { Handler = _ => new HostCommandResult(1, string.Empty, "Operation not permitted") };

        var e = await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).ApplyAsync("a1", Samples.Rules()));

        Assert.Contains("Operation not permitted", e.Message);
        Assert.Single(root.Commands);
    }

    [Fact]
    public async Task Apply_throws_when_the_list_command_fails()
    {
        var root = Echo();
        var echo = root.Handler;
        root.Handler = c => c.Arguments[0] == "list" ? new HostCommandResult(1, string.Empty, "No such file") : echo(c);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).ApplyAsync("a1", Samples.Rules()));
    }

    [Fact]
    public async Task A_runner_exception_surfaces()
    {
        var root = new FakeRoot { Handler = _ => throw new EgressEnforcementException("nft was not found") };

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root).BlockAllAsync("a1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_guest_interface_cannot_be_enforced(string? iface)
    {
        var root = Echo();

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Create(root, iface).ApplyAsync("a1", Samples.Rules()));

        Assert.Empty(root.Commands);
    }

    [Theory]
    [InlineData("tap0\" drop; flush ruleset; \"")]
    [InlineData("a b")]
    [InlineData("tap0\n")]
    [InlineData("averyveryverylonginterface")]
    public async Task A_hostile_interface_name_is_rejected_before_nft_runs(string iface)
    {
        var root = Echo();

        await Assert.ThrowsAsync<ArgumentException>(() => Create(root, iface).ApplyAsync("a1", Samples.Rules()));

        Assert.Empty(root.Commands);
    }

    [Fact]
    public async Task An_agent_id_with_a_trailing_newline_is_hashed_not_used_as_a_table_name()
    {
        var root = Echo();

        await Create(root).ApplyAsync("a1\n", Samples.Rules());

        Assert.StartsWith("bazaar_egress_h", root.Commands[1].Arguments[3]);
        Assert.True(NftRootCommandRunner.IsAllowed(root.Commands[1].Arguments));
    }

    [Fact]
    public async Task An_agent_id_that_is_not_a_plain_identifier_gets_a_hashed_tag_that_passes_the_allow_list()
    {
        var root = Echo();
        const string agent = "agent-1; flush ruleset";

        await Create(root).ApplyAsync(agent, Samples.Rules());

        var table = root.Commands[1].Arguments[3];
        Assert.StartsWith("bazaar_egress_h", table);
        Assert.True(NftRootCommandRunner.IsAllowed(root.Commands[1].Arguments));
        Assert.DoesNotContain("flush", root.Commands[0].StandardInput);
        Assert.NotEqual(NftablesEgressEnforcer.TagFor(agent), NftablesEgressEnforcer.TagFor("agent-2; flush ruleset"));
        Assert.Equal(NftablesEgressEnforcer.TagFor(agent), NftablesEgressEnforcer.TagFor(agent));
    }

    [Fact]
    public void Normalisation_folds_white_space_and_the_named_priority_and_ignores_the_replace_header()
    {
        var generated = NftablesRuleset.Generate("a1", "tap0", Samples.Rules());
        var listed = "table bridge bazaar_egress_a1 {\n\tchain input {\n\t\ttype filter hook input priority filter; policy accept;\n";

        Assert.Contains("type filter hook input priority -200; policy accept;", NftablesEgressEnforcer.Normalize(generated));
        Assert.Contains("type filter hook input priority -200; policy accept;", NftablesEgressEnforcer.Normalize(listed));
        Assert.DoesNotContain("delete table", NftablesEgressEnforcer.Normalize(generated));
    }

    [Fact]
    public async Task Calls_for_one_agent_do_not_overlap()
    {
        var root = Echo();
        root.Delay = TimeSpan.FromMilliseconds(15);
        var enforcer = Create(root);

        await Task.WhenAll(enforcer.ApplyAsync("a1", Samples.Rules()), enforcer.BlockAllAsync("a1"));

        // Serialised: one call's load and listing are never split by the other call's load.
        Assert.Equal(["-f", "list", "-f", "list"], root.Commands.Select(c => c.Arguments[0]));
    }
}
