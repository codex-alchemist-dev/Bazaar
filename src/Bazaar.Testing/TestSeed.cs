using System.Globalization;

namespace Bazaar.Testing;

/// <summary>
/// The seed for one randomised test run. A failing run logs its seed, and <see cref="EnvironmentVariable"/> replays it.
/// </summary>
public readonly record struct TestSeed(ulong Value)
{
    /// <summary>Environment variable that pins the seed, for example <c>BAZAAR_TEST_SEED=12345</c>.</summary>
    public const string EnvironmentVariable = "BAZAAR_TEST_SEED";

    /// <summary>A new unpredictable seed for a run that did not pin one.</summary>
    public static TestSeed NewRandom() => new((ulong)Random.Shared.NextInt64());

    /// <summary>
    /// Resolves the seed from the value of <see cref="EnvironmentVariable"/>. Unset or blank means a new random seed.
    /// A value that is not an unsigned integer is an error, so a typo never silently changes the run.
    /// </summary>
    public static TestSeed Resolve(string? environmentValue)
    {
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            return NewRandom();
        }

        if (!ulong.TryParse(environmentValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException(
                $"{EnvironmentVariable} must be an unsigned integer, got '{environmentValue}'.");
        }

        return new TestSeed(value);
    }

    /// <summary>The seed from the process environment, or a new random seed when none is pinned.</summary>
    public static TestSeed FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
