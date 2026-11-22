namespace Bazaar.Guest.PoolSpawn;

/// <summary>The few descriptor calls the shuffle needs. A simulated table stands in for it in tests.</summary>
internal interface IFdOps
{
    /// <summary>Duplicates <paramref name="fd"/> to the lowest free descriptor at or above <paramref name="minimum"/>, close-on-exec. Returns the new descriptor.</summary>
    int DuplicateAtLeast(int fd, int minimum);

    /// <summary>Makes <paramref name="target"/> a copy of <paramref name="source"/>; the copy is not close-on-exec.</summary>
    void Duplicate2(int source, int target);

    /// <summary>Closes a descriptor.</summary>
    void Close(int fd);
}

/// <summary>
/// Moves two freshly opened descriptors onto 3 and 4 without losing either, whatever numbers <c>open()</c> returned (it may have
/// returned 3 or 4 itself, even swapped). Anything in 0..4 is first moved above them, then both are copied to place, then the
/// spare copies are closed. The copies on 3 and 4 are inheritable across <c>execve</c>; every other descriptor opened here is closed.
/// </summary>
internal static class FdShuffle
{
    /// <summary>The descriptor the read end must end on (the browser reads DevTools commands here).</summary>
    public const int ReadTarget = 3;

    /// <summary>The descriptor the write end must end on (the browser writes replies here).</summary>
    public const int WriteTarget = 4;

    private const int _safeMinimum = 10;

    /// <summary>Puts <paramref name="readFd"/> on 3 and <paramref name="writeFd"/> on 4.</summary>
    public static void Run(IFdOps ops, int readFd, int writeFd)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (readFd < 0 || writeFd < 0 || readFd == writeFd)
        {
            throw new ArgumentException("The two descriptors must be open and different.");
        }

        var read = readFd <= WriteTarget ? ops.DuplicateAtLeast(readFd, _safeMinimum) : readFd;
        var write = writeFd <= WriteTarget ? ops.DuplicateAtLeast(writeFd, _safeMinimum) : writeFd;

        // Both copies exist now, so closing the low originals cannot lose either one.
        if (readFd <= WriteTarget)
        {
            ops.Close(readFd);
        }

        if (writeFd <= WriteTarget)
        {
            ops.Close(writeFd);
        }

        ops.Duplicate2(read, ReadTarget);
        ops.Duplicate2(write, WriteTarget);
        ops.Close(read);
        ops.Close(write);
    }
}
