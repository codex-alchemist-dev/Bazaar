namespace Bazaar.OfflineSync;

/// <summary>A hybrid logical clock timestamp: wall time, a counter for ties, and the device id. Ordered totally. Doc 56 section 3.</summary>
public readonly record struct Hlc(long WallMillis, int Counter, Guid DeviceId) : IComparable<Hlc>
{
    /// <inheritdoc />
    public int CompareTo(Hlc other)
    {
        var c = WallMillis.CompareTo(other.WallMillis);
        if (c != 0)
        {
            return c;
        }

        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : DeviceId.CompareTo(other.DeviceId);
    }

    /// <summary>Whether <paramref name="a"/> is later than <paramref name="b"/>.</summary>
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;

    /// <summary>Whether <paramref name="a"/> is earlier than <paramref name="b"/>.</summary>
    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;

    /// <summary>Whether <paramref name="a"/> is later than or equal to <paramref name="b"/>.</summary>
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;

    /// <summary>Whether <paramref name="a"/> is earlier than or equal to <paramref name="b"/>.</summary>
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;
}

/// <summary>A per-device hybrid logical clock. Thread safe.</summary>
public sealed class HlcClock(Guid deviceId, Hlc? initial = null)
{
    private readonly Lock _gate = new();
    private long _wall = initial?.WallMillis ?? 0;
    private int _counter = initial?.Counter ?? 0;

    /// <summary>The device this clock stamps for.</summary>
    public Guid DeviceId { get; } = deviceId;

    /// <summary>Stamps a local event at <paramref name="nowMillis"/>. Never goes backwards.</summary>
    public Hlc Tick(long nowMillis)
    {
        lock (_gate)
        {
            if (nowMillis > _wall)
            {
                _wall = nowMillis;
                _counter = 0;
            }
            else
            {
                _counter++;
            }

            return new Hlc(_wall, _counter, DeviceId);
        }
    }

    /// <summary>Merges a remote stamp into this clock and stamps the receive event.</summary>
    public Hlc Receive(Hlc remote, long nowMillis)
    {
        lock (_gate)
        {
            var wall = Math.Max(Math.Max(_wall, remote.WallMillis), nowMillis);
            if (wall == _wall && wall == remote.WallMillis)
            {
                _counter = Math.Max(_counter, remote.Counter) + 1;
            }
            else if (wall == _wall)
            {
                _counter++;
            }
            else if (wall == remote.WallMillis)
            {
                _counter = remote.Counter + 1;
            }
            else
            {
                _counter = 0;
            }

            _wall = wall;
            return new Hlc(_wall, _counter, DeviceId);
        }
    }
}
