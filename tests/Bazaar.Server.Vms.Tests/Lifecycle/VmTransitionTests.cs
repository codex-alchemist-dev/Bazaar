using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>The transition table, checked edge by edge against doc 16.</summary>
public sealed class VmTransitionTests
{
    private static readonly (VmState From, VmState To)[] _allowed =
    [
        (VmState.Absent, VmState.Provisioning),
        (VmState.Provisioning, VmState.Stopped),
        (VmState.Provisioning, VmState.Absent),
        (VmState.Stopped, VmState.Running),
        (VmState.Running, VmState.Stopped),
        (VmState.Running, VmState.Saved),
        (VmState.Saved, VmState.Running),
        (VmState.Stopped, VmState.Checkpointed),
        (VmState.Saved, VmState.Checkpointed),
        (VmState.Checkpointed, VmState.Restored),
        (VmState.Restored, VmState.Running),
        (VmState.Restored, VmState.Stopped),
        (VmState.Stopped, VmState.Retired),
        (VmState.Saved, VmState.Retired),
        (VmState.Checkpointed, VmState.Retired),
        (VmState.Restored, VmState.Retired),
    ];

    public static TheoryData<VmState, VmState> AllowedEdges()
    {
        var data = new TheoryData<VmState, VmState>();
        foreach (var (from, to) in _allowed)
            data.Add(from, to);
        return data;
    }

    public static TheoryData<VmState, VmState> ForbiddenSample() => new()
    {
        { VmState.Running, VmState.Checkpointed },
        { VmState.Running, VmState.Retired },
        { VmState.Running, VmState.Running },
        { VmState.Running, VmState.Absent },
        { VmState.Absent, VmState.Running },
        { VmState.Absent, VmState.Retired },
        { VmState.Absent, VmState.Stopped },
        { VmState.Provisioning, VmState.Running },
        { VmState.Provisioning, VmState.Retired },
        { VmState.Stopped, VmState.Stopped },
        { VmState.Stopped, VmState.Saved },
        { VmState.Stopped, VmState.Restored },
        { VmState.Saved, VmState.Stopped },
        { VmState.Saved, VmState.Saved },
        { VmState.Checkpointed, VmState.Running },
        { VmState.Checkpointed, VmState.Stopped },
        { VmState.Checkpointed, VmState.Checkpointed },
        { VmState.Restored, VmState.Checkpointed },
        { VmState.Restored, VmState.Saved },
        { VmState.Retired, VmState.Absent },
        { VmState.Retired, VmState.Provisioning },
        { VmState.Retired, VmState.Stopped },
        { VmState.Retired, VmState.Running },
        { VmState.Retired, VmState.Saved },
        { VmState.Retired, VmState.Checkpointed },
        { VmState.Retired, VmState.Restored },
        { VmState.Retired, VmState.Retired },
    };

    public static TheoryData<VmState> AllStates()
    {
        var data = new TheoryData<VmState>();
        foreach (var state in Enum.GetValues<VmState>())
            data.Add(state);
        return data;
    }

    [Fact]
    public void Allowed_set_is_exactly_the_documented_edges()
    {
        var states = Enum.GetValues<VmState>();
        var allowed = new HashSet<(VmState, VmState)>(_allowed);

        foreach (var from in states)
        {
            foreach (var to in states)
                Assert.Equal(allowed.Contains((from, to)), VmTransitions.CanTransition(from, to));
        }
    }

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void Allowed_edge_is_permitted(VmState from, VmState to) =>
        Assert.True(VmTransitions.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(ForbiddenSample))]
    public void Forbidden_edge_is_refused(VmState from, VmState to) =>
        Assert.False(VmTransitions.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Retired_leads_nowhere(VmState to) =>
        Assert.False(VmTransitions.CanTransition(VmState.Retired, to));
}
