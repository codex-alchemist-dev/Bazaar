using Bazaar.Ex;

namespace Bazaar.Ex.Tests;

public class ClientCommandDispatcherTests
{
    [Fact]
    public async Task The_first_handler_that_claims_the_id_runs_it_and_returns_its_notice()
    {
        var dispatcher = new ClientCommandDispatcher();
        var ran = new List<string>();
        dispatcher.Add(id => id.StartsWith("a.", StringComparison.Ordinal), (id, _) =>
        {
            ran.Add("first:" + id);
            return Task.FromResult<string?>("note");
        });
        dispatcher.Add(_ => true, (id, _) =>
        {
            ran.Add("second:" + id);
            return Task.FromResult<string?>(null);
        });

        var result = await dispatcher.DispatchAsync("a.run", TestContext.Current.CancellationToken);

        Assert.True(result.Handled);
        Assert.Equal("note", result.Notice);
        Assert.Equal(["first:a.run"], ran);
    }

    [Fact]
    public async Task An_unclaimed_id_is_reported_not_thrown()
    {
        var dispatcher = new ClientCommandDispatcher();
        dispatcher.Add(_ => false, (_, _) => Task.FromResult<string?>(null));

        var result = await dispatcher.DispatchAsync("other.cmd", TestContext.Current.CancellationToken);

        Assert.False(result.Handled);
        Assert.Contains("other.cmd", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handler_that_throws_becomes_a_notice()
    {
        var dispatcher = new ClientCommandDispatcher();
        dispatcher.Add(_ => true, (_, _) => throw new InvalidOperationException("boom"));

        var result = await dispatcher.DispatchAsync("x.y", TestContext.Current.CancellationToken);

        Assert.True(result.Handled);
        Assert.Contains("boom", result.Notice, StringComparison.Ordinal);
    }
}
