using System.Text.RegularExpressions;

namespace Bazaar.Secrets;

/// <summary>Stable identity of one agent. Each agent owns one secret scope and one data key.</summary>
public sealed partial record AgentId
{
    public AgentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!IdPattern().IsMatch(value))
        {
            throw new ArgumentException("Agent ids are 1 to 63 lowercase letters, digits or hyphens, starting with a letter or digit.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
