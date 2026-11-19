using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// Light sanity check on the wake-depth read (doc 38, Chain depth). It checks the SQL names the two tables and the column the
/// read depends on. The database behaviour is covered by <see cref="RyuseiWakeDepthSourceTests"/>.
/// </summary>
public sealed class RyuseiWakeDepthSourceSqlTests
{
    [Fact]
    public void Chain_depth_sql_joins_fire_to_trigger_and_reads_chain_depth()
    {
        var sql = RyuseiWakeDepthSource.ChainDepthSql;

        Assert.Contains("ryusei_fire", sql, StringComparison.Ordinal);
        Assert.Contains("ryusei_trigger", sql, StringComparison.Ordinal);
        Assert.Contains("chain_depth", sql, StringComparison.Ordinal);
        Assert.Contains("JOIN ryusei_trigger", sql, StringComparison.Ordinal);
        Assert.Contains("@fire_id", sql, StringComparison.Ordinal);
    }
}
