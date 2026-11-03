namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>Estimates the tokens a text costs against a section budget. Must be deterministic for a given text.</summary>
public interface ITokenEstimator
{
    int Estimate(string text);
}

/// <summary>The default estimate: one token per four characters, rounded up. Deterministic and tokenizer-free.</summary>
public sealed class CharQuarterTokenEstimator : ITokenEstimator
{
    public static CharQuarterTokenEstimator Instance { get; } = new();

    public int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3) / 4;
    }
}
