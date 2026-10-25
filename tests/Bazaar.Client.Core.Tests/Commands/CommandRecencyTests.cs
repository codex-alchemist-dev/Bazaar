using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.Core.Tests.Commands;

public class CommandRecencyTests
{
    [Fact]
    public void Touch_moves_id_to_front_without_duplicating()
    {
        var recency = new CommandRecency();

        recency.Touch("a");
        recency.Touch("b");
        recency.Touch("a");

        Assert.Equal(["a", "b"], recency.Ids);
        Assert.Equal(0, recency.Rank("a"));
        Assert.Equal(1, recency.Rank("b"));
        Assert.Equal(-1, recency.Rank("missing"));
    }

    [Fact]
    public void Capacity_trims_the_oldest_entries()
    {
        var recency = new CommandRecency(2);

        recency.Touch("a");
        recency.Touch("b");
        recency.Touch("c");

        Assert.Equal(["c", "b"], recency.Ids);
        Assert.Equal(-1, recency.Rank("a"));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var recency = new CommandRecency();
        recency.Touch("a");

        recency.Clear();

        Assert.Empty(recency.Ids);
    }

    [Fact]
    public void Non_positive_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandRecency(0));
    }
}
