using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Host.Coding;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class RunningTurnParentDirectoryTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_running_turn_maps_to_the_parent_the_runtime_needs()
    {
        var registry = new InProcessRunningTurnRegistry();
        var turn = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var session = Guid.NewGuid();
        using var _ = registry.Register(new RunningTurnInfo(turn, agent, session, "holder-1", ["read"], 1, 9, "gc-1", 4, "route/x"));

        var parent = await new RunningTurnParentDirectory(registry).FindAsync(turn, _ct);

        Assert.NotNull(parent);
        Assert.Equal((agent, session, "holder-1", 9, "gc-1", 4L), (parent.AgentId, parent.SessionId, parent.ParentHolder, parent.ParentStepCap, parent.ParentTriggerGcId, parent.ParentTriggerGcSeq));
        Assert.Equal(["read"], parent.ParentToolAllowlist);
    }

    [Fact]
    public async Task An_unknown_turn_is_not_found_so_the_spawn_is_refused()
    {
        var directory = new RunningTurnParentDirectory(new InProcessRunningTurnRegistry());

        Assert.Null(await directory.FindAsync(Guid.NewGuid(), _ct));
    }

    [Fact]
    public async Task A_turn_that_has_ended_is_no_longer_found()
    {
        var registry = new InProcessRunningTurnRegistry();
        var turn = Guid.NewGuid();
        registry.Register(new RunningTurnInfo(turn, Guid.NewGuid(), Guid.NewGuid(), "h", [], 0, 5, null, null, "")).Dispose();

        Assert.Null(await new RunningTurnParentDirectory(registry).FindAsync(turn, _ct));
    }

    [Fact]
    public async Task The_extension_binds_the_directory_to_the_shared_registry()
    {
        using var provider = new ServiceCollection().AddRunningTurnParentDirectory().BuildServiceProvider();
        var turn = Guid.NewGuid();
        using var _ = provider.GetRequiredService<IRunningTurnRegistrar>()
            .Register(new RunningTurnInfo(turn, Guid.NewGuid(), Guid.NewGuid(), "h", ["a"], 0, 5, null, null, ""));

        var directory = provider.GetRequiredService<IRuntimeSubagentParentDirectory>();

        Assert.IsType<RunningTurnParentDirectory>(directory);
        Assert.NotNull(await directory.FindAsync(turn, _ct));
        Assert.Null(await directory.FindAsync(Guid.NewGuid(), _ct));
    }

    [Fact]
    public void A_directory_the_host_registered_first_is_kept()
    {
        var own = new RunningTurnParentDirectory(new InProcessRunningTurnRegistry());
        using var provider = new ServiceCollection()
            .AddSingleton<IRuntimeSubagentParentDirectory>(own)
            .AddRunningTurnParentDirectory()
            .BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<IRuntimeSubagentParentDirectory>());
    }
}
