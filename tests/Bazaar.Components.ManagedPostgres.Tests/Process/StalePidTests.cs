namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class StalePidTests
{
    [Fact]
    public void A_live_process_that_is_not_postgres_is_not_a_cluster_to_adopt()
    {
        // A postmaster.pid left behind by a crash can name a pid the OS has since given to another program.
        // The running test host is alive and is not postgres.
        var runner = new ProcessPostgresCommandRunner();

        Assert.False(runner.IsProcessAlive(Environment.ProcessId));
    }
}
