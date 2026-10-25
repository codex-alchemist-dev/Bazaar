using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>
/// Seeds for the GC property tests: a fixed regression list, a deterministic sweep, and the seed from
/// <see cref="TestSeed.EnvironmentVariable"/> when it is set.
/// </summary>
internal static class GcPropertySeeds
{
    /// <summary>Random sequences per property in the sweep.</summary>
    public const int SweepCount = 200;

    /// <summary>Operations per sequence (after the bootstrap).</summary>
    public const int OpsPerSequence = 300;

    /// <summary>Seeds that have caught bugs or cover edge shapes. Never remove one.</summary>
    private static readonly ulong[] Regression =
    [
        1,
        2,
        3,
        42,
        1337,
        0xBAD5EED,
        0xFEEDFACE,
        0x0123456789ABCDEF,
    ];

    /// <summary>Every seed a property runs: regression first, then the sweep, then the environment seed when set.</summary>
    public static IEnumerable<TestSeed> All()
    {
        foreach (var value in Regression)
        {
            yield return new TestSeed(value);
        }

        var derive = new DeterministicIds(new TestSeed(0x6A09E667F3BCC908));
        for (var i = 0; i < SweepCount; i++)
        {
            yield return new TestSeed(derive.NextUInt64());
        }

        var pinned = Environment.GetEnvironmentVariable(TestSeed.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            yield return TestSeed.Resolve(pinned);
        }
    }
}
