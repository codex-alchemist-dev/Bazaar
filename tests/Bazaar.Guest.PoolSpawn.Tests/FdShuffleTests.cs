using Bazaar.Guest.PoolSpawn;

namespace Bazaar.Guest.PoolSpawn.Tests;

public sealed class FdShuffleTests
{
    /// <summary>A simulated descriptor table: each entry maps a number to the object it refers to and its close-on-exec flag.</summary>
    private sealed class SimTable : IFdOps
    {
        public Dictionary<int, (string Object, bool CloExec)> Fds { get; } = [];

        public int DuplicateAtLeast(int fd, int minimum)
        {
            var n = minimum;
            while (Fds.ContainsKey(n))
            {
                n++;
            }

            Fds[n] = (Fds[fd].Object, true);
            return n;
        }

        public void Duplicate2(int source, int target) => Fds[target] = (Fds[source].Object, false);

        public void Close(int fd) => Assert.True(Fds.Remove(fd), "closed a descriptor that was not open");
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(6, 5)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(3, 7)]
    [InlineData(7, 3)]
    [InlineData(4, 8)]
    [InlineData(8, 4)]
    [InlineData(0, 1)]
    [InlineData(20, 21)]
    public void Both_descriptors_end_on_3_and_4_inheritable_and_nothing_else_is_left(int readFd, int writeFd)
    {
        var table = new SimTable();
        foreach (var inherited in Enumerable.Range(0, 3).Where(f => f != readFd && f != writeFd))
        {
            table.Fds[inherited] = ("std" + inherited, false);
        }

        table.Fds[readFd] = ("read-fifo", true);
        table.Fds[writeFd] = ("write-fifo", true);

        FdShuffle.Run(table, readFd, writeFd);

        Assert.Equal(("read-fifo", false), table.Fds[3]);
        Assert.Equal(("write-fifo", false), table.Fds[4]);
        // Every descriptor above 4 that was opened for this is closed again.
        Assert.DoesNotContain(table.Fds.Keys, k => k > 4);
    }

    [Fact]
    public void Descriptors_that_were_not_open_are_refused()
    {
        var table = new SimTable();
        Assert.Throws<ArgumentException>(() => FdShuffle.Run(table, -1, 5));
        Assert.Throws<ArgumentException>(() => FdShuffle.Run(table, 5, 5));
    }
}
