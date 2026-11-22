using Bazaar.Kodo.Incidents;

namespace Bazaar.Kodo.Tests.Incidents;

public sealed class IncidentStateMachineTests
{
    private static readonly HashSet<(IncidentPhase, IncidentPhase)> _valid =
    [
        (IncidentPhase.Opened, IncidentPhase.Frozen),
        (IncidentPhase.Frozen, IncidentPhase.Investigating),
        (IncidentPhase.Investigating, IncidentPhase.Recovering),
        (IncidentPhase.Investigating, IncidentPhase.Resolved),
        (IncidentPhase.Recovering, IncidentPhase.Investigating),
        (IncidentPhase.Recovering, IncidentPhase.Resolved),
        (IncidentPhase.Resolved, IncidentPhase.Investigating),
        (IncidentPhase.Resolved, IncidentPhase.Closed),
    ];

    public static TheoryData<IncidentPhase, IncidentPhase> AllPairs()
    {
        TheoryData<IncidentPhase, IncidentPhase> data = [];
        foreach (IncidentPhase from in Enum.GetValues<IncidentPhase>())
        {
            foreach (IncidentPhase to in Enum.GetValues<IncidentPhase>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPairIsValidOnlyWhenListed(IncidentPhase from, IncidentPhase to)
    {
        Assert.Equal(_valid.Contains((from, to)), IncidentStateMachine.IsValid(from, to));
    }

    [Fact]
    public void ClosedIsFinalAndNextListsMatchTheTable()
    {
        Assert.Empty(IncidentStateMachine.Next(IncidentPhase.Closed));
        foreach (IncidentPhase from in Enum.GetValues<IncidentPhase>())
        {
            Assert.Equal(_valid.Count(pair => pair.Item1 == from), IncidentStateMachine.Next(from).Count);
        }
    }

    [Fact]
    public void AnUndefinedPhaseHasNoMoves()
    {
        Assert.False(IncidentStateMachine.IsValid((IncidentPhase)99, IncidentPhase.Frozen));
        Assert.Empty(IncidentStateMachine.Next((IncidentPhase)99));
    }
}
