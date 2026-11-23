using System.Text.RegularExpressions;

namespace Bazaar.Tabula;

/// <summary>
/// Name of one tamper-evident chain (doc 85 R-14). Each named chain is hashed and verified independently of every other.
/// Lowercase letters, digits, '.', '_' and '-', starting with a letter, at most 64 characters.
/// </summary>
public readonly partial record struct ChainId
{
    /// <summary>Name of the single chain used when a host does not need several.</summary>
    public const string DefaultName = "default";

    private readonly string? _value;

    public ChainId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!NamePattern().IsMatch(value))
        {
            throw new ArgumentException("Chain id must match ^[a-z][a-z0-9._-]{0,63}$.", nameof(value));
        }

        _value = value;
    }

    /// <summary>The chain name. <c>default(ChainId)</c> reads as the default chain.</summary>
    public string Value => _value ?? DefaultName;

    public static ChainId Default => new(DefaultName);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
