using System.Buffers.Binary;

namespace Bazaar.Testing;

/// <summary>
/// Produces ids that depend only on the seed, so the same seed gives the same ids in the same order on every
/// machine and runtime version. Uses SplitMix64 rather than <see cref="Random"/>, whose sequence is not a contract.
/// Thread-safe: concurrent callers draw from one sequence, and the order is then up to scheduling.
/// </summary>
public sealed class DeterministicIds
{
    private readonly object _gate = new();
    private ulong _state;

    public DeterministicIds(TestSeed seed)
    {
        _state = seed.Value;
    }

    /// <summary>The next 64 bits of the sequence.</summary>
    public ulong NextUInt64()
    {
        lock (_gate)
        {
            return NextUnlocked();
        }
    }

    /// <summary>A GUID built from the next 128 bits of the sequence.</summary>
    public Guid NextGuid()
    {
        var bytes = new byte[16];
        lock (_gate)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), NextUnlocked());
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), NextUnlocked());
        }

        return new Guid(bytes);
    }

    /// <summary>An id such as <c>agent_3f2a…</c>: the prefix, an underscore, then 32 lowercase hex digits.</summary>
    public string NextId(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return $"{prefix}_{NextGuid():N}";
    }

    /// <summary>A value in <c>[0, maxExclusive)</c>. Uses rejection sampling, so every value is equally likely.</summary>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);

        var bound = (ulong)maxExclusive;
        var limit = ulong.MaxValue - (ulong.MaxValue % bound);
        ulong value;
        lock (_gate)
        {
            do
            {
                value = NextUnlocked();
            }
            while (value >= limit);
        }

        return (int)(value % bound);
    }

    private ulong NextUnlocked()
    {
        _state += 0x9E3779B97F4A7C15; // SplitMix64 increment (golden ratio, 2^64 / phi)
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
