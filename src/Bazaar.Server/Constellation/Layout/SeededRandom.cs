namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// SplitMix64 generator. Its output is fixed by the seed alone, so layouts do not depend on the runtime's Random implementation.
/// </summary>
internal struct SeededRandom
{
    private ulong _state;

    public SeededRandom(int seed)
    {
        _state = unchecked((ulong)(uint)seed) * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
    }

    /// <summary>Returns a value in [0, 1).</summary>
    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
