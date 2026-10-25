namespace Bazaar.Testing;

/// <summary>
/// Runs a seeded test body. If the body throws, the seed (and the clock, when given) is written to the log so the
/// failure can be replayed with <see cref="TestSeed.EnvironmentVariable"/>. The original exception is rethrown unchanged.
/// </summary>
public static class SeededRun
{
    /// <summary>The key under which the seed is stored in <see cref="Exception.Data"/>.</summary>
    public const string SeedDataKey = "Bazaar.Testing.Seed";

    public static void Execute(TestSeed seed, Action<TestSeed> body, FakeClock? clock = null, TextWriter? log = null) =>
        Execute(seed, s => { body(s); return 0; }, clock, log);

    public static T Execute<T>(TestSeed seed, Func<TestSeed, T> body, FakeClock? clock = null, TextWriter? log = null)
    {
        try
        {
            return body(seed);
        }
        catch (Exception ex)
        {
            ex.Data[SeedDataKey] = seed.Value;
            (log ?? Console.Error).WriteLine(Describe(seed, clock));
            throw;
        }
    }

    private static string Describe(TestSeed seed, FakeClock? clock)
    {
        var clockText = clock is null ? string.Empty : $" clock={clock}";
        return $"Bazaar test failed: seed={seed}{clockText}. Replay with {TestSeed.EnvironmentVariable}={seed}.";
    }
}
