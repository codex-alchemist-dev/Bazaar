namespace Bazaar.Server.Gateway.Selection;

/// <summary>Source of randomness, injectable so tests are deterministic under a seed. Must be thread safe.</summary>
public interface ISelectionRandom
{
    /// <summary>A value in [0, 1).</summary>
    double NextDouble();
}

/// <summary>The default source: the shared, thread-safe system generator.</summary>
public sealed class SystemSelectionRandom : ISelectionRandom
{
    public static SystemSelectionRandom Instance { get; } = new();

    public double NextDouble() => Random.Shared.NextDouble();
}

/// <summary>A seeded generator behind a lock, for tests and replay.</summary>
public sealed class SeededSelectionRandom(int seed) : ISelectionRandom
{
    private readonly Random _random = new(seed);
    private readonly Lock _gate = new();

    public double NextDouble()
    {
        lock (_gate)
        {
            return _random.NextDouble();
        }
    }
}

internal static class RandomExtensions
{
    /// <summary>Standard normal by Box-Muller.</summary>
    public static double NextGaussian(this ISelectionRandom random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Gamma(shape, 1) by Marsaglia and Tsang, for shape of at least 1.</summary>
    public static double NextGamma(this ISelectionRandom random, double shape)
    {
        var d = shape - 1.0 / 3.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x;
            double v;
            do
            {
                x = random.NextGaussian();
                v = 1.0 + c * x;
            }
            while (v <= 0);

            v = v * v * v;
            var u = random.NextDouble();
            if (u < 1.0 - 0.0331 * x * x * x * x || Math.Log(Math.Max(u, double.Epsilon)) < 0.5 * x * x + d * (1.0 - v + Math.Log(v)))
            {
                return d * v;
            }
        }
    }

    /// <summary>Beta(a, b) sample for a and b of at least 1.</summary>
    public static double NextBeta(this ISelectionRandom random, double a, double b)
    {
        var x = random.NextGamma(a);
        var y = random.NextGamma(b);
        return x / (x + y);
    }
}
