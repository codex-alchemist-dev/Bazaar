namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>
/// PBKDF2 cost for sealing a recovery bundle. The floor is a security invariant: a setting may raise the cost but never
/// lower it below <see cref="MinimumIterations"/> (doc 48 section 6; configurability rule).
/// </summary>
public sealed record KdfOptions
{
    /// <summary>The lowest iteration count accepted when sealing or unsealing. Also the shipped default.</summary>
    public const int MinimumIterations = 600_000;

    /// <summary>The highest iteration count accepted. Guards against a header that would stall unsealing.</summary>
    public const int MaximumIterations = 10_000_000;

    /// <summary>The shipped default cost.</summary>
    public static KdfOptions Default { get; } = new(MinimumIterations);

    /// <summary>Creates options. Throws when the iteration count is outside the floor and ceiling.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is below the floor or above the ceiling.</exception>
    public KdfOptions(int iterations)
    {
        if (iterations < MinimumIterations || iterations > MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                $"PBKDF2 iterations must be between {MinimumIterations} and {MaximumIterations}.");
        }

        Iterations = iterations;
    }

    /// <summary>PBKDF2-HMAC-SHA256 iteration count.</summary>
    public int Iterations { get; }
}
