using Bazaar.Server.Agents.Turns.Tools.Db;

namespace Bazaar.Server.Tests.Agents.Turns.Tools.Db;

/// <summary>Pure rules for the in-flight table: the state strings, the lease length and the unfinished rule.</summary>
public class InFlightCallRowsTests
{
    [Fact]
    public void Lease_is_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), InFlightCallRows.Lease);
    }

    [Fact]
    public void State_strings_match_the_migration_check_list()
    {
        Assert.Equal("begun", InFlightCallRows.Begun);
        Assert.Equal("completed", InFlightCallRows.Completed);
    }

    [Fact]
    public void Only_a_begun_row_is_unfinished()
    {
        Assert.True(InFlightCallRows.IsUnfinished(InFlightCallRows.Begun));
        Assert.False(InFlightCallRows.IsUnfinished(InFlightCallRows.Completed));
        Assert.False(InFlightCallRows.IsUnfinished(null));
    }
}
