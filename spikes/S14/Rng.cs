namespace S14;

/// <summary>SplitMix64. Fixed, portable sequence so graphs and seeds are identical on every run and machine.</summary>
public struct Rng
{
    private ulong _state;

    public Rng(ulong seed) => _state = seed;

    public ulong NextU64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, n).</summary>
    public int NextInt(int n) => (int)((NextU64() >> 33) % (ulong)n);

    /// <summary>Uniform in [0, 1).</summary>
    public float NextFloat() => (NextU64() >> 40) * (1.0f / (1UL << 24));
}
