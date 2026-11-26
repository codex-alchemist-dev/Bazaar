namespace Bazaar.Server.ReaderSessions;

/// <summary>
/// Settings the doc leaves open. Each default is the most protective value; an operator setting may tighten a limit and the
/// defaults are not a floor to loosen below. Hard taint, zero tools and purge-at-end are not options.
/// </summary>
public sealed record ReaderSessionOptions
{
    /// <summary>Longest input text accepted, in characters. Longer input is refused, not truncated.</summary>
    public int MaxInputChars { get; init; } = 100_000;

    /// <summary>Longest reply kept, in characters. A longer reply fails the session. Matches the 64 KB hand-over cap.</summary>
    public int MaxReplyChars { get; init; } = 65_536;

    /// <summary>Output token cap sent to the gateway.</summary>
    public int MaxOutputTokens { get; init; } = 4_096;

    /// <summary>Sampling temperature. Zero keeps readers as deterministic as the provider allows.</summary>
    public double Temperature { get; init; }
}
