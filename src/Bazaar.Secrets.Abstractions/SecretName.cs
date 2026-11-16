using System.Text.RegularExpressions;

namespace Bazaar.Secrets;

/// <summary>Name of one secret inside a scope. Names are lowercase and never carry values.</summary>
public sealed partial record SecretName
{
    public SecretName(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!NamePattern().IsMatch(value))
        {
            throw new ArgumentException("Secret names are 1 to 128 lowercase letters, digits, dots, underscores or hyphens.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
