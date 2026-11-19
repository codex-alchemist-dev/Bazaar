using System.Text.RegularExpressions;

namespace Bazaar.Secrets;

/// <summary>Why a host component reads a secret, for example <c>gateway.call</c>. Every read names one; the audit log records it.</summary>
public sealed partial record SecretPurpose
{
    public SecretPurpose(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!PurposePattern().IsMatch(value))
        {
            throw new ArgumentException("Purposes are 1 to 64 lowercase letters, digits, dots, underscores or hyphens, starting with a letter.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();
}
