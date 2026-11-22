namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// The bytes one root holds, as this <see cref="ModuleFileSystem"/> instance counts them. The count is measured once, on the
/// first write to the root, by walking it; after that it follows the writes, truncations and deletes made through this
/// instance. It covers writes through this instance only: a file that something else adds, grows or removes behind its back is
/// seen at the next instance, not before. The count may run high (a reservation for bytes a failed write never wrote is
/// refunded where the stream can tell) but never on purpose low. Every change is made under one lock per root, so two writers
/// cannot pass the quota together.
/// </summary>
internal sealed class ModuleFsRootUsage(long maxBytes, Func<long> measure)
{
    private readonly Lock _gate = new();
    private long _bytes;
    private bool _measured;

    /// <summary>Measures the root now if that has not happened yet. A failed measurement is not remembered, so the next call retries.</summary>
    public void EnsureMeasured()
    {
        lock (_gate)
        {
            MeasureLocked();
        }
    }

    /// <summary>Refuses when growing the root by <paramref name="bytes"/> would pass the quota; changes nothing.</summary>
    public void Check(long bytes)
    {
        lock (_gate)
        {
            MeasureLocked();
            if (bytes > 0 && bytes > maxBytes - _bytes)
            {
                throw new ModuleFsRefusedException("The root would be larger than its limit.");
            }
        }
    }

    /// <summary>Counts a growth of <paramref name="bytes"/>, or refuses it before anything is written.</summary>
    public void Charge(long bytes)
    {
        lock (_gate)
        {
            MeasureLocked();
            if (bytes > maxBytes - _bytes)
            {
                throw new ModuleFsRefusedException("The root would be larger than its limit.");
            }

            _bytes += bytes;
        }
    }

    /// <summary>Gives back bytes a file no longer holds. Before the first measurement there is nothing to give back: the walk will see the truth.</summary>
    public void Release(long bytes) => Adjust(-bytes);

    /// <summary>Corrects the count by a signed amount without a quota check, after a write that did less than was reserved.</summary>
    public void Adjust(long bytes)
    {
        lock (_gate)
        {
            if (_measured)
            {
                _bytes = Math.Max(0, _bytes + bytes);
            }
        }
    }

    /// <summary>Runs a delete that returns the length it removed, under the lock, and gives that length back.</summary>
    public void Deleting(Func<long> delete)
    {
        lock (_gate)
        {
            var removed = delete();
            if (_measured)
            {
                _bytes = Math.Max(0, _bytes - removed);
            }
        }
    }

    private void MeasureLocked()
    {
        if (_measured)
        {
            return;
        }

        _bytes = measure();
        _measured = true;
    }
}
