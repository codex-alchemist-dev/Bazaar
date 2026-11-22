using Bazaar.Client.Hive.Grants;
using Bazaar.Client.Hive.Panes;

namespace Bazaar.Client.Hive.Tests;

public class HiveGrantActionsTests
{
    private static readonly HiveGrantKey _key = new("alpha", "beta", "procedural");

    [Fact]
    public async Task Grant_request_reaches_the_actions_with_the_right_key()
    {
        var fake = new FakeHiveGrantActions();
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model), fake);

        await pane.RequestAsync(_key, grant: true, TestContext.Current.CancellationToken);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("grant", call.Op);
        Assert.Equal(_key, call.Key);
        Assert.Equal("Requested. The matrix changes when the server confirms.", pane.LastStatus);
    }

    [Fact]
    public async Task Revoke_request_reaches_the_actions_as_a_revoke()
    {
        var fake = new FakeHiveGrantActions();
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model), fake);

        await pane.RequestAsync(_key, grant: false, TestContext.Current.CancellationToken);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("revoke", call.Op);
        Assert.Equal(_key, call.Key);
    }

    [Fact]
    public async Task A_refusal_is_shown_and_the_matrix_does_not_change()
    {
        var fake = new FakeHiveGrantActions { Result = new HiveGrantResult(false, "grant is under review") };
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model), fake);
        var before = pane.Matrix;

        await pane.RequestAsync(_key, grant: true, TestContext.Current.CancellationToken);

        Assert.Equal("Refused: grant is under review", pane.LastStatus);
        Assert.Same(before, pane.Matrix);
    }

    [Fact]
    public async Task A_lost_connection_leaves_the_grant_unchanged()
    {
        var fake = new FakeHiveGrantActions { Result = null };
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model), fake);

        await pane.RequestAsync(_key, grant: true, TestContext.Current.CancellationToken);

        Assert.Equal("Could not reach the server. The grant is unchanged.", pane.LastStatus);
    }

    [Fact]
    public async Task Without_actions_the_pane_sends_nothing_and_reports_it()
    {
        var pane = new HivePaneViewModel(HiveTestData.Descriptor(HiveTestData.Model));

        await pane.RequestAsync(_key, grant: true, TestContext.Current.CancellationToken);

        Assert.False(pane.CanChangeGrants);
        Assert.Equal("Grant changes are not available in this client.", pane.LastStatus);
    }

    [Fact]
    public void Registry_wires_the_head_supplied_actions_into_the_pane()
    {
        var fake = new FakeHiveGrantActions();
        var pane = Assert.IsType<HivePaneViewModel>(HiveTestData.CreateRegistry(fake).Create(HiveTestData.Descriptor(HiveTestData.Model)));

        Assert.True(pane.CanChangeGrants);
    }
}
