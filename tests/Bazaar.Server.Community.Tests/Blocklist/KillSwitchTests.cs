using Bazaar.Server.Community.Blocklist;

namespace Bazaar.Server.Community.Tests.Blocklist;

/// <summary>Kill switch behaviour: objections, platform and agent stops, and resume. Uses the in-memory blocklist.</summary>
public class KillSwitchTests
{
    private static readonly string _platform = "reddit";
    private static readonly string _target = "r/example/comments/abc123";

    private static Task<KillSwitchDecision> CheckKill(KillSwitch kill, string agentId, string platform, string? targetRef) =>
        kill.CheckAsync(agentId, platform, targetRef, TestContext.Current.CancellationToken);

    private static Task<KillSwitchDecision> ObjectTarget(KillSwitch kill, string platform, string targetRef, string reason, string addedBy) =>
        kill.ObjectAsync(platform, targetRef, reason, addedBy, TestContext.Current.CancellationToken);

    private static Task<bool> IsBlocked(InMemoryTargetBlocklist blocklist, string platform, string targetRef) =>
        blocklist.IsBlockedAsync(platform, targetRef, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Objection_blocks_target_and_stays_blocked()
    {
        var blocklist = new InMemoryTargetBlocklist();
        var kill = new KillSwitch(blocklist);

        var decision = await ObjectTarget(kill, _platform, _target, "owner objection", "owner");
        Assert.True(decision.Blocked);
        Assert.Equal(KillSwitch.TargetScope, decision.Scope);
        Assert.Equal("owner objection", decision.Reason);

        for (var i = 0; i < 3; i++)
        {
            var check = await CheckKill(kill, "agent-1", _platform, _target);
            Assert.True(check.Blocked);
            Assert.Equal(KillSwitch.TargetScope, check.Scope);
        }

        // Another agent is blocked too: the target list is not per agent.
        Assert.True((await CheckKill(kill, "agent-2", _platform, _target)).Blocked);
        Assert.True(await IsBlocked(blocklist, _platform, _target));
    }

    [Fact]
    public async Task Objection_does_not_block_other_targets_on_the_platform()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        await ObjectTarget(kill, _platform, _target, "owner objection", "owner");

        var other = await CheckKill(kill, "agent-1", _platform, "r/example/comments/zzz999");
        Assert.False(other.Blocked);
        Assert.Null(other.Scope);
        Assert.Null(other.Reason);
    }

    [Fact]
    public async Task Repeated_objection_keeps_the_first_reason()
    {
        var blocklist = new InMemoryTargetBlocklist();
        var kill = new KillSwitch(blocklist);

        await ObjectTarget(kill, _platform, _target, "first reason", "owner");
        var second = await ObjectTarget(kill, _platform, _target, "second reason", "moderator");

        Assert.True(second.Blocked);
        Assert.True(await IsBlocked(blocklist, _platform, _target));
    }

    [Fact]
    public async Task Platform_stop_blocks_all_targets_there_including_no_target()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        kill.StopPlatform(_platform, "platform notice");

        var withTarget = await CheckKill(kill, "agent-1", _platform, "t-1");
        Assert.True(withTarget.Blocked);
        Assert.Equal(KillSwitch.PlatformScope, withTarget.Scope);
        Assert.Equal("platform notice", withTarget.Reason);

        var withoutTarget = await CheckKill(kill, "agent-1", _platform, null);
        Assert.True(withoutTarget.Blocked);
        Assert.Equal(KillSwitch.PlatformScope, withoutTarget.Scope);

        // A different platform is not affected.
        Assert.False((await CheckKill(kill, "agent-1", "github", "t-1")).Blocked);
    }

    [Fact]
    public async Task Agent_stop_blocks_only_that_agent_on_every_platform()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        kill.StopAgent("agent-1", "agent misbehaving");

        var stopped = await CheckKill(kill, "agent-1", _platform, "t-1");
        Assert.True(stopped.Blocked);
        Assert.Equal(KillSwitch.AgentScope, stopped.Scope);

        Assert.True((await CheckKill(kill, "agent-1", "github", null)).Blocked);
        Assert.False((await CheckKill(kill, "agent-2", _platform, "t-1")).Blocked);
    }

    [Fact]
    public async Task Resume_platform_restores_writes_on_that_platform()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        kill.StopPlatform(_platform, "platform notice");
        Assert.True((await CheckKill(kill, "agent-1", _platform, "t-1")).Blocked);

        kill.ResumePlatform(_platform);

        Assert.False((await CheckKill(kill, "agent-1", _platform, "t-1")).Blocked);
    }

    [Fact]
    public async Task Resume_agent_restores_that_agent()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        kill.StopAgent("agent-1", "agent misbehaving");
        Assert.True((await CheckKill(kill, "agent-1", _platform, null)).Blocked);

        kill.ResumeAgent("agent-1");

        Assert.False((await CheckKill(kill, "agent-1", _platform, null)).Blocked);
    }

    [Fact]
    public async Task Resume_does_not_lift_an_objection()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        await ObjectTarget(kill, _platform, _target, "owner objection", "owner");
        kill.StopPlatform(_platform, "platform notice");
        kill.ResumePlatform(_platform);

        var check = await CheckKill(kill, "agent-1", _platform, _target);
        Assert.True(check.Blocked);
        Assert.Equal(KillSwitch.TargetScope, check.Scope);
    }

    [Fact]
    public async Task Platform_stop_is_reported_before_agent_and_target()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());
        await ObjectTarget(kill, _platform, _target, "owner objection", "owner");
        kill.StopAgent("agent-1", "agent misbehaving");
        kill.StopPlatform(_platform, "platform notice");

        var check = await CheckKill(kill, "agent-1", _platform, _target);
        Assert.Equal(KillSwitch.PlatformScope, check.Scope);
    }

    [Fact]
    public async Task Allowed_decision_has_no_scope_or_reason()
    {
        var kill = new KillSwitch(new InMemoryTargetBlocklist());

        var check = await CheckKill(kill, "agent-1", _platform, _target);

        Assert.Same(KillSwitchDecision.Allowed, check);
        Assert.False(check.Blocked);
        Assert.Null(check.Scope);
        Assert.Null(check.Reason);
    }
}
